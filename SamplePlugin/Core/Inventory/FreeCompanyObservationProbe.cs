using Autofac;
using CriticalCommonLib.GameStructs;
using CriticalCommonLib.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Linq;

namespace FROG.Core.Inventory;

/// <summary>
/// Diagnostic-only probe used to compare the FC chest state exposed by the
/// raw game inventory, CriticalCommonLib's cache and FROG's persisted index.
/// It does not change inventory state or observation/promotion decisions.
/// </summary>
internal sealed class FreeCompanyObservationProbe : IDisposable
{
    private const int ProbePollIntervalMilliseconds = 10;

    private readonly Plugin plugin;
    private readonly IInventoryScanner inventoryScanner;
    private readonly ICharacterMonitor characterMonitor;
    private readonly long sessionStartedAtMs = Environment.TickCount64;

    private bool disposed;
    private bool chestWasOpen;
    private uint? selectedContainer;
    private long selectedContainerSinceMs;
    private long probeGeneration;
    private long lastPollAtMs;
    private string? lastStateSignature;

    public FreeCompanyObservationProbe(
        Plugin plugin,
        IInventoryScanner inventoryScanner,
        ICharacterMonitor characterMonitor)
    {
        this.plugin = plugin;
        this.inventoryScanner = inventoryScanner;
        this.characterMonitor = characterMonitor;

        inventoryScanner.ContainerInfoReceived += OnContainerInfoReceived;
        Plugin.Framework.Update += OnFrameworkUpdate;

        FreeCompanyObservationDiagnostics.Add(
            "PROBE START");
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;

        inventoryScanner.ContainerInfoReceived -= OnContainerInfoReceived;
        Plugin.Framework.Update -= OnFrameworkUpdate;

        FreeCompanyObservationDiagnostics.Add(
            "PROBE STOP");
    }

    private void OnFrameworkUpdate(
        Dalamud.Plugin.Services.IFramework framework)
    {
        var nowMs = Environment.TickCount64;

        if (nowMs - lastPollAtMs < ProbePollIntervalMilliseconds)
            return;

        lastPollAtMs = nowMs;

        if (!TryGetSelectedFreeCompanyPage(out var container))
        {
            if (chestWasOpen)
            {
                FreeCompanyObservationDiagnostics.Add(
                    $"PROBE CHEST_CLOSE sessionMs={ElapsedSessionMs(nowMs)}");
            }

            chestWasOpen = false;
            selectedContainer = null;
            selectedContainerSinceMs = 0;
            lastStateSignature = null;
            return;
        }

        if (!chestWasOpen)
        {
            chestWasOpen = true;

            FreeCompanyObservationDiagnostics.Add(
                $"PROBE CHEST_OPEN sessionMs={ElapsedSessionMs(nowMs)}");
        }

        if (selectedContainer != container)
        {
            var previous = selectedContainer;

            selectedContainer = container;
            selectedContainerSinceMs = nowMs;
            probeGeneration++;
            lastStateSignature = null;

            FreeCompanyObservationDiagnostics.Add(
                $"PROBE PAGE previous={FormatContainer(previous)} current={FormatContainer(container)} probeGen={probeGeneration} sessionMs={ElapsedSessionMs(nowMs)} pageMs=0");

            CaptureState(
                container,
                nowMs,
                "page-change",
                force: true);

            return;
        }

        CaptureState(
            container,
            nowMs,
            "poll",
            force: false);
    }

    private void OnContainerInfoReceived(
        ContainerInfo containerInfo,
        InventoryType inventoryType)
    {
        if (!IsFreeCompanyPage(inventoryType))
            return;

        var nowMs = Environment.TickCount64;
        var uiContainer = selectedContainer;

        FreeCompanyObservationDiagnostics.Add(
            $"PROBE CONTAINER_INFO page={FormatContainer((uint)inventoryType)} ui={FormatContainer(uiContainer)} probeGen={probeGeneration} sessionMs={ElapsedSessionMs(nowMs)} pageMs={ElapsedPageMs(nowMs, uiContainer)} seq={containerInfo.containerSequence} numItems={containerInfo.numItems} startOrFinish={containerInfo.startOrFinish}");

        CaptureState(
            (uint)inventoryType,
            nowMs,
            "container-info",
            force: true);
    }

    private unsafe void CaptureState(
        uint container,
        long nowMs,
        string reason,
        bool force)
    {
        var inventoryType = (InventoryType)container;
        var freeCompanyId = characterMonitor.ActiveFreeCompanyId;

        var rawAvailable = false;
        var rawLoaded = false;
        var rawItems = 0;
        long rawQuantity = 0;
        ulong rawFingerprint = 14695981039346656037UL;

        var inventoryManager = InventoryManager.Instance();

        if (inventoryManager != null)
        {
            var rawContainer = inventoryManager->GetInventoryContainer(inventoryType);

            if (rawContainer != null)
            {
                rawAvailable = true;
                rawLoaded = rawContainer->IsLoaded;

                for (var slot = 0; slot < rawContainer->Size; slot++)
                {
                    var item = rawContainer->Items[slot];

                    if (item.ItemId == 0)
                        continue;

                    rawItems++;
                    rawQuantity += item.Quantity;
                    rawFingerprint = MixFingerprint(
                        rawFingerprint,
                        item.ItemId,
                        item.Quantity,
                        slot);
                }
            }
        }

        var cclLoaded = inventoryScanner.IsBagLoaded(inventoryType);
        var cclInMemory = inventoryScanner.InMemory.Contains(inventoryType);
        var cclItems = 0;
        long cclQuantity = 0;
        ulong cclFingerprint = 14695981039346656037UL;

        var cclSnapshots = inventoryScanner.GetInventoryByType(inventoryType);

        for (var slot = 0; slot < cclSnapshots.Length; slot++)
        {
            var item = cclSnapshots[slot];

            if (item.ItemId == 0)
                continue;

            cclItems++;
            cclQuantity += item.Quantity;
            cclFingerprint = MixFingerprint(
                cclFingerprint,
                item.ItemId,
                item.Quantity,
                slot);
        }

        var knownSnapshots = plugin.InventoryIndex.Items
            .Where(item =>
                item.Storage == StorageType.FreeCompanyChest &&
                item.OwnerId == freeCompanyId &&
                item.Container == container)
            .ToList();

        var knownItems = knownSnapshots.Count;
        var knownQuantity = knownSnapshots.Sum(item => (long)item.Quantity);
        var knownFingerprint = BuildKnownFingerprint(knownSnapshots);

        DateTime? knownObservedAtUtc = null;

        if (freeCompanyId != 0)
        {
            knownObservedAtUtc = plugin.InventoryIndex.GetSourceObservedAtUtc(
                new InventorySource(
                    StorageType.FreeCompanyChest,
                    freeCompanyId,
                    container,
                    characterMonitor.ActiveCharacterId));
        }

        var signature = string.Join(
            "|",
            container,
            rawAvailable,
            rawLoaded,
            rawItems,
            rawQuantity,
            rawFingerprint,
            cclLoaded,
            cclInMemory,
            cclItems,
            cclQuantity,
            cclFingerprint,
            freeCompanyId,
            knownItems,
            knownQuantity,
            knownFingerprint,
            knownObservedAtUtc.HasValue);

        if (!force && string.Equals(signature, lastStateSignature, StringComparison.Ordinal))
            return;

        lastStateSignature = signature;

        var knownFreshness = knownObservedAtUtc.HasValue
            ? $"session observedUtc={knownObservedAtUtc.Value:O} ageMs={Math.Max(0L, (long)(DateTime.UtcNow - knownObservedAtUtc.Value).TotalMilliseconds)}"
            : "persisted-or-not-observed-this-session";

        FreeCompanyObservationDiagnostics.Add(
            $"PROBE STATE reason={reason} page={FormatContainer(container)} ui={FormatContainer(selectedContainer)} probeGen={probeGeneration} sessionMs={ElapsedSessionMs(nowMs)} pageMs={ElapsedPageMs(nowMs, selectedContainer)} fc={freeCompanyId} RAW[available={rawAvailable} loaded={rawLoaded} items={rawItems} qty={rawQuantity} fp={rawFingerprint:X8}] CCL[loaded={cclLoaded} inMemory={cclInMemory} items={cclItems} qty={cclQuantity} fp={cclFingerprint:X8}] KNOWN[items={knownItems} qty={knownQuantity} fp={knownFingerprint:X8} freshness={knownFreshness}]");
    }

    private static ulong BuildKnownFingerprint(
        System.Collections.Generic.IReadOnlyList<InventoryItemSnapshot> snapshots)
    {
        var fingerprint = 14695981039346656037UL;

        foreach (var item in snapshots
                     .OrderBy(item => item.Slot)
                     .ThenBy(item => item.RawItemId)
                     .ThenBy(item => item.Quantity))
        {
            fingerprint = MixFingerprint(
                fingerprint,
                item.RawItemId,
                item.Quantity,
                item.Slot);
        }

        return fingerprint;
    }

    private static ulong MixFingerprint(
        ulong fingerprint,
        uint itemId,
        int quantity,
        int slot)
    {
        const ulong prime = 1099511628211UL;

        unchecked
        {
            fingerprint = (fingerprint ^ itemId) * prime;
            fingerprint = (fingerprint ^ (uint)quantity) * prime;
            fingerprint = (fingerprint ^ (uint)slot) * prime;
        }

        return fingerprint;
    }

    private long ElapsedSessionMs(long nowMs) =>
        Math.Max(0, nowMs - sessionStartedAtMs);

    private long ElapsedPageMs(
        long nowMs,
        uint? container)
    {
        if (!container.HasValue ||
            selectedContainer != container ||
            selectedContainerSinceMs == 0)
        {
            return -1;
        }

        return Math.Max(0, nowMs - selectedContainerSinceMs);
    }

    private static bool IsFreeCompanyPage(
        InventoryType inventoryType) =>
        inventoryType is
            InventoryType.FreeCompanyPage1 or
            InventoryType.FreeCompanyPage2 or
            InventoryType.FreeCompanyPage3 or
            InventoryType.FreeCompanyPage4 or
            InventoryType.FreeCompanyPage5;

    private static unsafe bool TryGetSelectedFreeCompanyPage(
        out uint container)
    {
        container = 0;

        var addon = Plugin.GameGui.GetAddonByName<AtkUnitBase>(
            "FreeCompanyChest",
            1);

        if (addon == null || !addon->IsVisible)
            return false;

        if (IsTabSelected(addon, 101))
            container = (uint)InventoryType.FreeCompanyPage1;
        else if (IsTabSelected(addon, 100))
            container = (uint)InventoryType.FreeCompanyPage2;
        else if (IsTabSelected(addon, 99))
            container = (uint)InventoryType.FreeCompanyPage3;
        else if (IsTabSelected(addon, 98))
            container = (uint)InventoryType.FreeCompanyPage4;
        else if (IsTabSelected(addon, 97))
            container = (uint)InventoryType.FreeCompanyPage5;
        else
            return false;

        return true;
    }

    private static unsafe bool IsTabSelected(
        AtkUnitBase* addon,
        uint nodeId)
    {
        if (nodeId >= addon->UldManager.NodeListCount)
            return false;

        var node = addon->UldManager.NodeList[nodeId];

        if (node == null || !node->IsVisible())
            return false;

        var componentNode = node->GetAsAtkComponentNode();

        if (componentNode == null || componentNode->Component == null)
            return false;

        var component = componentNode->Component;

        if (component->UldManager.NodeListCount > 2)
        {
            var checkMark = component->UldManager.NodeList[2];

            if (checkMark != null && checkMark->IsVisible())
                return true;
        }

        var radioButton = (AtkComponentRadioButton*)component;
        return (radioButton->Flags & 0x40000) != 0;
    }

    private static string FormatContainer(uint? container) =>
        container switch
        {
            (uint)InventoryType.FreeCompanyPage1 => "P1",
            (uint)InventoryType.FreeCompanyPage2 => "P2",
            (uint)InventoryType.FreeCompanyPage3 => "P3",
            (uint)InventoryType.FreeCompanyPage4 => "P4",
            (uint)InventoryType.FreeCompanyPage5 => "P5",
            null => "none",
            _ => container.Value.ToString()
        };
}
