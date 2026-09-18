using System;
using System.Collections.Generic;
using Dalamud.Game.Inventory;
using Dalamud.Plugin.Services;

namespace FROG.Core.Inventory.Providers;

public sealed class CharacterInventoryProvider : ICharacterInventoryProvider
{
    private readonly IGameInventory gameInventory;
    private readonly IPlayerState playerState;

    public CharacterInventoryProvider(
        IGameInventory gameInventory,
        IPlayerState playerState)
    {
        this.gameInventory = gameInventory;
        this.playerState = playerState;
    }

    public bool TryGetCurrentCharacter(
        out ulong characterId)
    {
        characterId = 0;

        if (!playerState.IsLoaded)
            return false;

        characterId = playerState.ContentId;

        return characterId != 0;
    }

    public IReadOnlyList<InventoryItemSnapshot> ReadCurrentInventory(
        ulong characterId,
        DateTime observedAtUtc)
    {
        var snapshots = new List<InventoryItemSnapshot>();

        AddInventorySnapshots(
            snapshots,
            GameInventoryType.Inventory1,
            characterId,
            observedAtUtc);

        AddInventorySnapshots(
            snapshots,
            GameInventoryType.Inventory2,
            characterId,
            observedAtUtc);

        AddInventorySnapshots(
            snapshots,
            GameInventoryType.Inventory3,
            characterId,
            observedAtUtc);

        AddInventorySnapshots(
            snapshots,
            GameInventoryType.Inventory4,
            characterId,
            observedAtUtc);

        return snapshots;
    }

    private void AddInventorySnapshots(
        List<InventoryItemSnapshot> snapshots,
        GameInventoryType inventoryType,
        ulong characterId,
        DateTime observedAtUtc)
    {
        var inventoryItems =
            gameInventory.GetInventoryItems(inventoryType);

        for (var slot = 0;
             slot < inventoryItems.Length;
             slot++)
        {
            var item = inventoryItems[slot];

            if (item.ItemId == 0)
                continue;

            snapshots.Add(
                new InventoryItemSnapshot(
                    item.BaseItemId,
                    item.ItemId,
                    item.Quantity,
                    item.IsHq,
                    StorageType.CharacterInventory,
                    characterId,
                    (uint)inventoryType,
                    slot,
                    observedAtUtc,
                    true));
        }
    }
}
