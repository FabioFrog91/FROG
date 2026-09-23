using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

/// <summary>
/// Bridges completed Dalamud character-inventory changelogs into FROG's
/// physical CharacterInventory snapshots. The notification and snapshot read
/// come from the same IGameInventory provider. Retainer observation has its
/// own RAW observer; Free Company observation keeps its dedicated CCL bridge.
/// </summary>
internal sealed class CharacterInventoryObservationObserver : IDisposable
{
    private readonly Plugin plugin;
    private readonly IGameInventory gameInventory;
    private bool disposed;

    public CharacterInventoryObservationObserver(
        Plugin plugin,
        IGameInventory gameInventory)
    {
        this.plugin = plugin;
        this.gameInventory = gameInventory;

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
            !events.Any(AffectsCharacterInventory))
        {
            return;
        }

        plugin.SyncPlayerInventory();
    }

    private static bool AffectsCharacterInventory(
        InventoryEventArgs inventoryEvent) =>
        inventoryEvent switch
        {
            InventoryItemAddedArgs added =>
                IsCharacterInventory(added.Inventory),

            InventoryItemRemovedArgs removed =>
                IsCharacterInventory(removed.Inventory),

            InventoryItemChangedArgs changed =>
                IsCharacterInventory(changed.Inventory),

            _ => false
        };

    private static bool IsCharacterInventory(
        GameInventoryType inventoryType) =>
        inventoryType is
            GameInventoryType.Inventory1 or
            GameInventoryType.Inventory2 or
            GameInventoryType.Inventory3 or
            GameInventoryType.Inventory4 or
            GameInventoryType.Crystals;
}
