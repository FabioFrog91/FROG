using CriticalCommonLib.Services;
using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Plugin.Services;
using FROG.Core.Inventory.Providers;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

/// <summary>
/// Owns Retainer observation for FROG.
/// CCL's loaded-retainer signal provides the initial acquisition point; raw
/// Dalamud inventory changes keep the physical snapshot current afterwards.
/// The snapshot itself is always read from the game's raw inventory view.
/// </summary>
internal sealed class RetainerInventoryObservationObserver : IDisposable
{
    private readonly Plugin plugin;
    private readonly IGameInventory gameInventory;
    private readonly ICharacterMonitor characterMonitor;
    private readonly StorageReaderAPI storageReader;
    private bool disposed;

    public RetainerInventoryObservationObserver(
        Plugin plugin,
        IGameInventory gameInventory,
        ICharacterMonitor characterMonitor,
        StorageReaderAPI storageReader)
    {
        this.plugin = plugin;
        this.gameInventory = gameInventory;
        this.characterMonitor = characterMonitor;
        this.storageReader = storageReader;

        characterMonitor.OnActiveRetainerLoaded +=
            OnActiveRetainerLoaded;

        gameInventory.InventoryChangedRaw +=
            OnInventoryChangedRaw;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;

        characterMonitor.OnActiveRetainerLoaded -=
            OnActiveRetainerLoaded;

        gameInventory.InventoryChangedRaw -=
            OnInventoryChangedRaw;
    }

    private void OnActiveRetainerLoaded(
        ulong retainerId)
    {
        if (disposed ||
            retainerId == 0)
        {
            return;
        }

        plugin.SyncStorageSources(
            storageReader);
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
