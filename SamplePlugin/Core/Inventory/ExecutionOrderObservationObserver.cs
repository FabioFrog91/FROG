using CriticalCommonLib.Models;
using CriticalCommonLib.Services;
using Dalamud.Plugin.Services;
using System;

namespace FROG.Core.Inventory;

/// <summary>
/// Persists live ItemOrderModule observations without changing inventory or
/// execution state. Active execution sessions remain immutable; refreshed
/// order is consumed by the next plan compilation.
/// </summary>
public sealed class ExecutionOrderObservationObserver : IDisposable
{
    private readonly IOdrScanner odrScanner;
    private readonly IPlayerState playerState;
    private readonly ExecutionOrderCatalog catalog;
    private readonly Plugin plugin;
    private bool disposed;

    public ExecutionOrderObservationObserver(
        IOdrScanner odrScanner,
        IPlayerState playerState,
        ExecutionOrderCatalog catalog,
        Plugin plugin)
    {
        this.odrScanner = odrScanner;
        this.playerState = playerState;
        this.catalog = catalog;
        this.plugin = plugin;

        odrScanner.OnSortOrderChanged +=
            OnSortOrderChanged;
    }

    private void OnSortOrderChanged(
        InventorySortOrder sortOrder)
    {
        var characterId =
            playerState.IsLoaded
                ? playerState.ContentId
                : 0;

        if (characterId == 0)
            return;

        if (!catalog.Observe(
                characterId,
                sortOrder))
        {
            return;
        }

        ForensicTraceRecorder.Record(
            "EXECUTION_ORDER",
            $"ODR_CACHE_REFRESH character={characterId}");

        plugin.SavePersistentState();
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;

        odrScanner.OnSortOrderChanged -=
            OnSortOrderChanged;
    }
}
