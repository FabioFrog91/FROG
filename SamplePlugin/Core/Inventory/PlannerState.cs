using System;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

public sealed class PlannerState
{
    private readonly List<InventoryItemSnapshot> items;

    public ulong CurrentCharacterId { get; }

    public IReadOnlyList<InventoryItemSnapshot> Items =>
        items.ToList();

    public PlannerState(
        ulong currentCharacterId,
        IEnumerable<InventoryItemSnapshot> items)
    {
        CurrentCharacterId = currentCharacterId;
        this.items = items.ToList();
    }

    public PlannerState WithCurrentCharacter(
        ulong characterId)
    {
        return new PlannerState(
            characterId,
            items);
    }

    public PlannerState WithItems(
        IEnumerable<InventoryItemSnapshot> newItems)
    {
        return new PlannerState(
            CurrentCharacterId,
            newItems);
    }

    public IReadOnlyList<InventoryItemSnapshot> Find(
        InventorySource source)
    {
        return items
            .Where(item =>
                item.Storage == source.Storage &&
                item.OwnerId == source.OwnerId &&
                item.Container == source.Container)
            .OrderBy(item => item.Slot)
            .ToList();
    }

    public IEnumerable<InventoryItemSnapshot> Find(
        uint baseItemId,
        bool isHq)
    {
        return items
            .Where(item =>
                item.BaseItemId == baseItemId &&
                item.IsHq == isHq)
            .ToList();
    }

    public int GetQuantity(
        uint baseItemId,
        bool isHq,
        InventorySource source)
    {
        return items
            .Where(item =>
                item.BaseItemId == baseItemId &&
                item.IsHq == isHq &&
                item.Storage == source.Storage &&
                item.OwnerId == source.OwnerId &&
                item.Container == source.Container)
            .Sum(item => item.Quantity);
    }

    public int GetMainInventoryQuantity(
        uint baseItemId,
        bool isHq)
    {
        return items
            .Where(item =>
                item.BaseItemId == baseItemId &&
                item.IsHq == isHq &&
                item.Storage == StorageType.CharacterInventory &&
                item.OwnerId == CurrentCharacterId)
            .Sum(item => item.Quantity);
    }

    public int GetTotalMainInventoryQuantity(
        uint baseItemId)
    {
        return items
            .Where(item =>
                item.BaseItemId == baseItemId &&
                item.Storage == StorageType.CharacterInventory &&
                item.OwnerId == CurrentCharacterId)
            .Sum(item => item.Quantity);
    }

    public int GetAvailableQuantity(
        uint baseItemId,
        bool isHq)
    {
        return items
            .Where(item =>
                item.BaseItemId == baseItemId &&
                item.IsHq == isHq)
            .Sum(item => item.Quantity);
    }

    public bool ContainsSource(
        InventorySource source)
    {
        return items.Any(item =>
            item.Storage == source.Storage &&
            item.OwnerId == source.OwnerId &&
            item.Container == source.Container);
    }

    public PlannerState Move(
        InventorySource source,
        InventorySource destination,
        uint baseItemId,
        bool isHq,
        int quantity)
    {
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity));

        var available = GetQuantity(
            baseItemId,
            isHq,
            source);

        if (available < quantity)
        {
            throw new InvalidOperationException(
                "The planner state does not contain enough items in the source.");
        }

        var updatedItems = items
            .Select(item => item)
            .ToList();

        var remaining = quantity;

        for (var i = 0; i < updatedItems.Count && remaining > 0; i++)
        {
            var item = updatedItems[i];

            if (item.BaseItemId != baseItemId ||
                item.IsHq != isHq ||
                item.Storage != source.Storage ||
                item.OwnerId != source.OwnerId ||
                item.Container != source.Container)
            {
                continue;
            }

            var moved = Math.Min(
                item.Quantity,
                remaining);

            var newQuantity = item.Quantity - moved;

            if (newQuantity == 0)
            {
                updatedItems.RemoveAt(i);
                i--;
            }
            else
            {
                updatedItems[i] =
                    item with
                    {
                        Quantity = newQuantity
                    };
            }

            remaining -= moved;
        }

        if (remaining != 0)
        {
            throw new InvalidOperationException(
                "The planner state could not consume the requested quantity.");
        }

        var destinationItemIndex =
            updatedItems.FindIndex(item =>
                item.BaseItemId == baseItemId &&
                item.IsHq == isHq &&
                item.Storage == destination.Storage &&
                item.OwnerId == destination.OwnerId &&
                item.Container == destination.Container);

        if (destinationItemIndex >= 0)
        {
            var destinationItem =
                updatedItems[destinationItemIndex];

            updatedItems[destinationItemIndex] =
                destinationItem with
                {
                    Quantity = destinationItem.Quantity + quantity
                };
        }
        else
        {
            var nextSlot =
                updatedItems
                    .Where(item =>
                        item.Storage == destination.Storage &&
                        item.OwnerId == destination.OwnerId &&
                        item.Container == destination.Container)
                    .Select(item => item.Slot)
                    .DefaultIfEmpty(-1)
                    .Max() + 1;

            updatedItems.Add(
                new InventoryItemSnapshot(
                    baseItemId,
                    0,
                    quantity,
                    isHq,
                    destination.Storage,
                    destination.OwnerId,
                    destination.Container,
                    nextSlot,
                    DateTime.UtcNow,
                    false));
        }

        return new PlannerState(
            CurrentCharacterId,
            updatedItems);
    }
}
