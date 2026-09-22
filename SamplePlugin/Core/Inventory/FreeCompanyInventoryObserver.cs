using CriticalCommonLib.Models;
using CriticalCommonLib.Services;
using FROG.Core.Inventory.Providers;
using FFXIVClientStructs.FFXIV.Client.Game;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

/// <summary>
/// Bridges CriticalCommonLib's session cache into FROG's persistent inventory index.
/// A free-company page is trusted only after CCL has actually scanned it into
/// <see cref="IInventoryScanner.InMemory"/>. UI selection and raw InventoryManager
/// state are intentionally not used as freshness barriers.
/// </summary>
internal sealed class FreeCompanyInventoryObserver : IDisposable
{
    private static readonly InventoryType[] FreeCompanyPages =
    {
        InventoryType.FreeCompanyPage1,
        InventoryType.FreeCompanyPage2,
        InventoryType.FreeCompanyPage3,
        InventoryType.FreeCompanyPage4,
        InventoryType.FreeCompanyPage5
    };

    private readonly Plugin plugin;
    private readonly IInventoryScanner inventoryScanner;
    private readonly ICharacterMonitor characterMonitor;
    private readonly StorageReaderAPI storageReader;

    private readonly HashSet<InventoryType> pendingInitialObservation = new();
    private readonly HashSet<InventoryType> observedThisSession = new();

    private bool disposed;
    private bool syncRequested;
    private ulong trackedFreeCompanyId;

    public FreeCompanyInventoryObserver(
        Plugin plugin,
        IInventoryScanner inventoryScanner,
        ICharacterMonitor characterMonitor,
        StorageReaderAPI storageReader)
    {
        this.plugin = plugin;
        this.inventoryScanner = inventoryScanner;
        this.characterMonitor = characterMonitor;
        this.storageReader = storageReader;

        trackedFreeCompanyId = characterMonitor.ActiveFreeCompanyId;

        inventoryScanner.ContainerInfoReceived += OnContainerInfoReceived;
        inventoryScanner.BagsChanged += OnBagsChanged;
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;

        inventoryScanner.ContainerInfoReceived -= OnContainerInfoReceived;
        inventoryScanner.BagsChanged -= OnBagsChanged;
        Plugin.Framework.Update -= OnFrameworkUpdate;

        pendingInitialObservation.Clear();
        observedThisSession.Clear();
    }

    private void OnContainerInfoReceived(
        CriticalCommonLib.GameStructs.ContainerInfo containerInfo,
        InventoryType inventoryType)
    {
        if (!IsFreeCompanyPage(inventoryType))
            return;

        if (!observedThisSession.Contains(inventoryType))
            pendingInitialObservation.Add(inventoryType);
    }

    private void OnBagsChanged(
        List<BagChange> changes)
    {
        // CCL has completed a scanner pass and changed at least one cached bag.
        // Syncing all already-observed FC pages is tiny (5 x 50 slots) and avoids
        // coupling FROG to BagChange's internal shape.
        syncRequested = true;
    }

    private void OnFrameworkUpdate(
        Dalamud.Plugin.Services.IFramework framework)
    {
        var freeCompanyId = characterMonitor.ActiveFreeCompanyId;

        if (freeCompanyId != trackedFreeCompanyId)
        {
            trackedFreeCompanyId = freeCompanyId;
            pendingInitialObservation.Clear();
            observedThisSession.Clear();
            syncRequested = freeCompanyId != 0;
        }

        if (freeCompanyId == 0)
            return;

        if (pendingInitialObservation.Count > 0)
        {
            foreach (var page in pendingInitialObservation.ToArray())
            {
                if (!inventoryScanner.InMemory.Contains(page))
                    continue;

                if (SyncPage(page, freeCompanyId, "initial"))
                {
                    pendingInitialObservation.Remove(page);
                    observedThisSession.Add(page);
                }
            }
        }

        if (!syncRequested)
            return;

        syncRequested = false;

        foreach (var page in FreeCompanyPages)
        {
            if (!inventoryScanner.InMemory.Contains(page))
                continue;

            if (SyncPage(page, freeCompanyId, "scanner-change"))
                observedThisSession.Add(page);
        }
    }

    private bool SyncPage(
        InventoryType page,
        ulong freeCompanyId,
        string reason)
    {
        var observedAtUtc = DateTime.UtcNow;

        if (!storageReader.TryReadActiveFreeCompanyPage(
                observedAtUtc,
                (uint)page,
                out var source,
                out var snapshots))
        {
            return false;
        }

        if (source.OwnerId != freeCompanyId)
            return false;

        var knownSnapshots = plugin.InventoryIndex.Items
            .Where(item =>
                item.Storage == source.Storage &&
                item.OwnerId == source.OwnerId &&
                item.Container == source.Container)
            .ToList();

        if (HaveSameContents(knownSnapshots, snapshots))
        {
            FreeCompanyObservationDiagnostics.Add(
                $"CCL_OBSERVED reason={reason} page={FormatPage(page)} owner={freeCompanyId} changed=False items={snapshots.Count} qty={snapshots.Sum(item => item.Quantity)}");

            return true;
        }

        plugin.InventoryIndex.ReplaceSource(
            source,
            snapshots,
            observedAtUtc);

        FreeCompanyObservationDiagnostics.Add(
            $"CCL_OBSERVED reason={reason} page={FormatPage(page)} owner={freeCompanyId} changed=True items={snapshots.Count} qty={snapshots.Sum(item => item.Quantity)}");

        return true;
    }

    private static bool HaveSameContents(
        IReadOnlyList<InventoryItemSnapshot> left,
        IReadOnlyList<InventoryItemSnapshot> right)
    {
        if (left.Count != right.Count)
            return false;

        var orderedLeft = left
            .OrderBy(item => item.Container)
            .ThenBy(item => item.Slot)
            .ThenBy(item => item.RawItemId)
            .ThenBy(item => item.Quantity)
            .ToArray();

        var orderedRight = right
            .OrderBy(item => item.Container)
            .ThenBy(item => item.Slot)
            .ThenBy(item => item.RawItemId)
            .ThenBy(item => item.Quantity)
            .ToArray();

        for (var index = 0; index < orderedLeft.Length; index++)
        {
            var a = orderedLeft[index];
            var b = orderedRight[index];

            if (a.BaseItemId != b.BaseItemId ||
                a.RawItemId != b.RawItemId ||
                a.Quantity != b.Quantity ||
                a.IsHq != b.IsHq ||
                a.Storage != b.Storage ||
                a.OwnerId != b.OwnerId ||
                a.Container != b.Container ||
                a.Slot != b.Slot ||
                a.ParentCharacterId != b.ParentCharacterId)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsFreeCompanyPage(
        InventoryType inventoryType) =>
        inventoryType is
            InventoryType.FreeCompanyPage1 or
            InventoryType.FreeCompanyPage2 or
            InventoryType.FreeCompanyPage3 or
            InventoryType.FreeCompanyPage4 or
            InventoryType.FreeCompanyPage5;

    private static string FormatPage(
        InventoryType page) =>
        page switch
        {
            InventoryType.FreeCompanyPage1 => "P1",
            InventoryType.FreeCompanyPage2 => "P2",
            InventoryType.FreeCompanyPage3 => "P3",
            InventoryType.FreeCompanyPage4 => "P4",
            InventoryType.FreeCompanyPage5 => "P5",
            _ => ((uint)page).ToString()
        };
}
