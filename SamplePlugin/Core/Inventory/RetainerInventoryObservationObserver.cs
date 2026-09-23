using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Plugin.Services;
using FROG.Core.Inventory.Providers;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

/// <summary>
/// Bridges completed Dalamud retainer-inventory changelogs into FROG's
/// physical Retainer snapshots. The notification and snapshot read both come
/// from the game's raw inventory view, so split/merge/relocation changes are
/// observed independently from CCL's display-oriented retainer cache.
/// </summary>
internal sealed class RetainerInventoryObservationObserver : IDisposable
{
    private readonly Plugin plugin;
    private readonly IGameInventory gameInventory;
    private readonly StorageReaderAPI storageReader;
    private bool disposed;

    public RetainerInventoryObservationObserver(
        Plugin plugin,
        IGameInventory gameInventory,
        StorageReaderAPI storageReader)
    {
        this.plugin = plugin;
        this.gameInventory = gameInventory;
        this.storageReader = storageReader;

        gameInventory.InventoryChangedRaw += OnInventoryChangedRaw;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        gameInventory.InventoryChangedRaw -= OnInventoryChangedRaw;
    }

    private void OnInventoryChangedRaw(
        IReadOnlyCollection<InventoryEventArgs> events)
    {
        if (disposed ||
            events.Count == 0 ||
            !events.Any(AffectsRetainerInventory))
        {
            return;
        }

        plugin.SyncStorageSources(
            storageReader);
    }

    private static bool AffectsRetainerInventory(
        InventoryEventArgs inventoryEvent) =>
        inventoryEvent switch
        {
            InventoryItemAddedArgs added =>
                IsRetainerInventory(added.Inventory),

            InventoryItemRemovedArgs removed =>
                IsRetainerInventory(removed.Inventory),

            InventoryItemChangedArgs changed =>
                IsRetainerInventory(changed.Inventory),

            _ => false
        };

    private static bool IsRetainerInventory(
        GameInventoryType inventoryType) =>
        inventoryType is
            GameInventoryType.RetainerPage1 or
            GameInventoryType.RetainerPage2 or
            GameInventoryType.RetainerPage3 or
            GameInventoryType.RetainerPage4 or
            GameInventoryType.RetainerPage5 or
            GameInventoryType.RetainerPage6 or
            GameInventoryType.RetainerPage7;
}
