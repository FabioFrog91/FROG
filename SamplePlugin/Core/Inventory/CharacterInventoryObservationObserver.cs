using CriticalCommonLib.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

/// <summary>
/// Bridges post-parse character bag changes from CCL into FROG's
/// CharacterInventory observation stream. No polling: a sync is requested
/// only when CCL reports a real change in Inventory1..4.
/// </summary>
internal sealed class CharacterInventoryObservationObserver : IDisposable
{
    private readonly Plugin plugin;
    private readonly IInventoryScanner inventoryScanner;
    private bool disposed;

    public CharacterInventoryObservationObserver(
        Plugin plugin,
        IInventoryScanner inventoryScanner)
    {
        this.plugin = plugin;
        this.inventoryScanner = inventoryScanner;

        inventoryScanner.BagsChanged += OnBagsChanged;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        inventoryScanner.BagsChanged -= OnBagsChanged;
    }

    private void OnBagsChanged(
        List<BagChange> changes)
    {
        if (!changes.Any(change => IsCharacterInventory(change.InventoryType)))
            return;

        plugin.SyncPlayerInventory();
    }

    private static bool IsCharacterInventory(
        InventoryType inventoryType) =>
        inventoryType is
            InventoryType.Inventory1 or
            InventoryType.Inventory2 or
            InventoryType.Inventory3 or
            InventoryType.Inventory4;
}
