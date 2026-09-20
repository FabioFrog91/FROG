using System;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

public sealed class PlannerState
{
    private readonly List<InventoryItemSnapshot> items;
    private readonly HashSet<ulong> visitedCharacters;

    public ulong MainCharacterId { get; }
    public ulong CurrentCharacterId { get; }
    public IReadOnlyList<InventoryItemSnapshot> Items => items;
    public IReadOnlyCollection<ulong> VisitedCharacters => visitedCharacters;

    public PlannerState(
        ulong mainCharacterId,
        ulong currentCharacterId,
        IEnumerable<InventoryItemSnapshot> items)
    {
        if (mainCharacterId == 0)
            throw new ArgumentOutOfRangeException(nameof(mainCharacterId));

        if (currentCharacterId == 0)
            throw new ArgumentOutOfRangeException(nameof(currentCharacterId));

        MainCharacterId = mainCharacterId;
        CurrentCharacterId = currentCharacterId;
        this.items = items.ToList();
        visitedCharacters = new HashSet<ulong>();

        if (currentCharacterId != mainCharacterId)
            visitedCharacters.Add(currentCharacterId);
    }

    private PlannerState(
        ulong mainCharacterId,
        ulong currentCharacterId,
        List<InventoryItemSnapshot> items,
        HashSet<ulong> visitedCharacters)
    {
        MainCharacterId = mainCharacterId;
        CurrentCharacterId = currentCharacterId;
        this.items = items;
        this.visitedCharacters = visitedCharacters;
    }

    public bool HasVisitedCharacter(ulong characterId) =>
        characterId != MainCharacterId &&
        visitedCharacters.Contains(characterId);

    public PlannerState WithCurrentCharacter(ulong characterId)
    {
        if (characterId == 0)
            throw new ArgumentOutOfRangeException(nameof(characterId));

        var updatedVisited = visitedCharacters.ToHashSet();

        if (characterId != MainCharacterId)
            updatedVisited.Add(characterId);

        return new PlannerState(
            MainCharacterId,
            characterId,
            items,
            updatedVisited);
    }

    public PlannerState WithItems(IEnumerable<InventoryItemSnapshot> newItems) =>
        new PlannerState(
            MainCharacterId,
            CurrentCharacterId,
            newItems.ToList(),
            visitedCharacters);

    public IReadOnlyList<InventoryItemSnapshot> Find(InventorySource source) =>
        items
            .Where(item =>
                item.Storage == source.Storage &&
                item.OwnerId == source.OwnerId &&
                item.Container == source.Container)
            .OrderBy(item => item.Slot)
            .ToList();

    public IEnumerable<InventoryItemSnapshot> Find(
        uint baseItemId,
        bool isHq) =>
        items.Where(item =>
            item.BaseItemId == baseItemId &&
            item.IsHq == isHq);

    public int GetQuantity(
        uint baseItemId,
        bool isHq,
        InventorySource source) =>
        items
            .Where(item =>
                item.BaseItemId == baseItemId &&
                item.IsHq == isHq &&
                item.Storage == source.Storage &&
                item.OwnerId == source.OwnerId &&
                item.Container == source.Container)
            .Sum(item => item.Quantity);

    public int GetMainInventoryQuantity(
        uint baseItemId,
        bool isHq) =>
        GetCharacterInventoryQuantity(
            MainCharacterId,
            baseItemId,
            isHq);

    public int GetCharacterInventoryQuantity(
        ulong characterId,
        uint baseItemId,
        bool isHq) =>
        items
            .Where(item =>
                item.BaseItemId == baseItemId &&
                item.IsHq == isHq &&
                item.Storage == StorageType.CharacterInventory &&
                item.OwnerId == characterId)
            .Sum(item => item.Quantity);

    public int GetFreeCompanyQuantity(
        uint baseItemId,
        bool isHq) =>
        items
            .Where(item =>
                item.BaseItemId == baseItemId &&
                item.IsHq == isHq &&
                item.Storage == StorageType.FreeCompanyChest)
            .Sum(item => item.Quantity);

    public int GetTotalMainInventoryQuantity(uint baseItemId) =>
        items
            .Where(item =>
                item.BaseItemId == baseItemId &&
                item.Storage == StorageType.CharacterInventory &&
                item.OwnerId == MainCharacterId)
            .Sum(item => item.Quantity);

    public int GetAvailableQuantity(
        uint baseItemId,
        bool isHq) =>
        items
            .Where(item =>
                item.BaseItemId == baseItemId &&
                item.IsHq == isHq)
            .Sum(item => item.Quantity);

    public bool ContainsSource(InventorySource source) =>
        items.Any(item =>
            item.Storage == source.Storage &&
            item.OwnerId == source.OwnerId &&
            item.Container == source.Container);

    public PlannerState Move(
        InventorySource source,
        InventorySource destination,
        uint baseItemId,
        bool isHq,
        int quantity)
    {
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity));

        var matchingSourceItems = items
            .Where(item =>
                item.BaseItemId == baseItemId &&
                item.IsHq == isHq &&
                item.Storage == source.Storage &&
                item.OwnerId == source.OwnerId &&
                item.Container == source.Container)
            .OrderBy(item => item.Slot)
            .ToList();

        if (matchingSourceItems.Sum(item => item.Quantity) < quantity)
        {
            throw new InvalidOperationException(
                "The planner state does not contain enough items in the source.");
        }

        var rawItemId = matchingSourceItems[0].RawItemId;
        var updatedItems = items.ToList();
        var remaining = quantity;

        foreach (var sourceItem in matchingSourceItems)
        {
            if (remaining == 0)
                break;

            var index = updatedItems.IndexOf(sourceItem);
            if (index < 0)
                continue;

            var moved = Math.Min(
                sourceItem.Quantity,
                remaining);

            var newQuantity = sourceItem.Quantity - moved;

            if (newQuantity == 0)
                updatedItems.RemoveAt(index);
            else
                updatedItems[index] = sourceItem with { Quantity = newQuantity };

            remaining -= moved;
        }

        if (remaining != 0)
        {
            throw new InvalidOperationException(
                "The planner state could not consume the requested quantity.");
        }

        var destinationItemIndex = updatedItems.FindIndex(item =>
            item.BaseItemId == baseItemId &&
            item.IsHq == isHq &&
            item.Storage == destination.Storage &&
            item.OwnerId == destination.OwnerId &&
            item.Container == destination.Container);

        if (destinationItemIndex >= 0)
        {
            var destinationItem = updatedItems[destinationItemIndex];

            updatedItems[destinationItemIndex] =
                destinationItem with
                {
                    Quantity = destinationItem.Quantity + quantity
                };
        }
        else
        {
            var nextSlot = updatedItems
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
                    rawItemId,
                    quantity,
                    isHq,
                    destination.Storage,
                    destination.OwnerId,
                    destination.Container,
                    nextSlot,
                    DateTime.UtcNow,
                    false,
                    destination.ParentCharacterId));
        }

        return new PlannerState(
            MainCharacterId,
            CurrentCharacterId,
            updatedItems,
            visitedCharacters);
    }
}
