using CriticalCommonLib.Services;
using FROG.Core.Inventory.Providers;
using FFXIVClientStructs.FFXIV.Client.Game;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

/// <summary>
/// Bridges completed CriticalCommonLib FC page scans into FROG's persistent Known state.
/// ContainerInfo creates an owner/generation-aware pending acquisition; only CCL's
/// post-copy FreeCompanyPageScanned event can complete it. Later content changes are
/// accepted only for pages already acquired in the current FC generation.
/// </summary>
internal sealed class FreeCompanyInventoryObserver : IDisposable
{
    private readonly record struct PendingObservation(
        ulong FreeCompanyId,
        InventoryType Page,
        long Generation);

    private readonly Plugin plugin;
    private readonly IInventoryScanner inventoryScanner;
    private readonly ICharacterMonitor characterMonitor;
    private readonly StorageReaderAPI storageReader;

    private readonly HashSet<PendingObservation> pending = new();
    private readonly HashSet<InventoryType> acquiredPages = new();

    private bool disposed;
    private ulong trackedFreeCompanyId;
    private long freeCompanyGeneration;

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
        freeCompanyGeneration = 1;

        inventoryScanner.ContainerInfoReceived += OnContainerInfoReceived;
        inventoryScanner.FreeCompanyPageScanned += OnFreeCompanyPageScanned;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;

        inventoryScanner.ContainerInfoReceived -= OnContainerInfoReceived;
        inventoryScanner.FreeCompanyPageScanned -= OnFreeCompanyPageScanned;

        pending.Clear();
        acquiredPages.Clear();
    }

    private void OnContainerInfoReceived(
        CriticalCommonLib.GameStructs.ContainerInfo containerInfo,
        InventoryType inventoryType)
    {
        if (disposed || !IsFreeCompanyPage(inventoryType))
            return;

        RefreshFreeCompanyIdentity();

        if (trackedFreeCompanyId == 0)
            return;

        var key = new PendingObservation(
            trackedFreeCompanyId,
            inventoryType,
            freeCompanyGeneration);

        pending.Add(key);

        FreeCompanyObservationDiagnostics.Add(
            $"FC_PENDING page={FormatPage(inventoryType)} owner={trackedFreeCompanyId} fcGen={freeCompanyGeneration} seq={containerInfo.containerSequence} numItems={containerInfo.numItems} startOrFinish={containerInfo.startOrFinish}");
    }

    private void OnFreeCompanyPageScanned(
        long scanRevision,
        InventoryType inventoryType,
        bool changed)
    {
        if (disposed || !IsFreeCompanyPage(inventoryType))
            return;

        RefreshFreeCompanyIdentity();

        var freeCompanyId = trackedFreeCompanyId;
        if (freeCompanyId == 0)
            return;

        var key = new PendingObservation(
            freeCompanyId,
            inventoryType,
            freeCompanyGeneration);

        var hasPending = pending.Contains(key);
        var alreadyAcquired = acquiredPages.Contains(inventoryType);

        // A periodic completed scan is not a new observation by itself.
        // Freshness advances only for a pending acquisition, or for a real
        // content change on a page already acquired for this FC generation.
        if (!hasPending && !(changed && alreadyAcquired))
        {
            if (changed && !alreadyAcquired)
            {
                FreeCompanyObservationDiagnostics.Add(
                    $"FC_SCAN_IGNORED page={FormatPage(inventoryType)} owner={freeCompanyId} fcGen={freeCompanyGeneration} scanRev={scanRevision} reason=changed-before-acquisition");
            }

            return;
        }

        if (!SyncPage(
                inventoryType,
                freeCompanyId,
                scanRevision,
                hasPending,
                changed))
        {
            return;
        }

        pending.Remove(key);
        acquiredPages.Add(inventoryType);
    }

    private bool SyncPage(
        InventoryType page,
        ulong freeCompanyId,
        long scanRevision,
        bool hadPending,
        bool providerChanged)
    {
        var observedAtUtc = DateTime.UtcNow;

        if (!storageReader.TryReadActiveFreeCompanyPage(
                observedAtUtc,
                (uint)page,
                out var source,
                out var snapshots))
        {
            FreeCompanyObservationDiagnostics.Add(
                $"FC_SCAN_IGNORED page={FormatPage(page)} owner={freeCompanyId} fcGen={freeCompanyGeneration} scanRev={scanRevision} reason=reader-rejected");
            return false;
        }

        if (source.OwnerId != freeCompanyId ||
            characterMonitor.ActiveFreeCompanyId != freeCompanyId)
        {
            FreeCompanyObservationDiagnostics.Add(
                $"FC_SCAN_IGNORED page={FormatPage(page)} owner={freeCompanyId} fcGen={freeCompanyGeneration} scanRev={scanRevision} reason=owner-changed");
            return false;
        }

        var result = plugin.InventoryIndex.ApplyObservation(
            source,
            snapshots,
            scanRevision,
            observedAtUtc);

        if (!result.Applied)
        {
            FreeCompanyObservationDiagnostics.Add(
                $"FC_SCAN_IGNORED page={FormatPage(page)} owner={freeCompanyId} fcGen={freeCompanyGeneration} scanRev={scanRevision} reason=stale-revision");
            return false;
        }

        FreeCompanyObservationDiagnostics.Add(
            $"FC_OBSERVED page={FormatPage(page)} owner={freeCompanyId} fcGen={freeCompanyGeneration} scanRev={scanRevision} pending={hadPending} providerChanged={providerChanged} contentChanged={result.ContentChanged} obsRev={result.ObservationRevision} contentRev={result.ContentRevision} items={snapshots.Count} qty={snapshots.Sum(item => item.Quantity)}");

        return true;
    }

    private void RefreshFreeCompanyIdentity()
    {
        var currentFreeCompanyId = characterMonitor.ActiveFreeCompanyId;

        if (currentFreeCompanyId == trackedFreeCompanyId)
            return;

        var previous = trackedFreeCompanyId;
        trackedFreeCompanyId = currentFreeCompanyId;
        freeCompanyGeneration++;
        pending.Clear();
        acquiredPages.Clear();

        FreeCompanyObservationDiagnostics.Add(
            $"FC_IDENTITY previous={previous} current={currentFreeCompanyId} fcGen={freeCompanyGeneration}");
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
