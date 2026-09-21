using System;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

public sealed class PlannerState
{
    private readonly List<InventoryItemSnapshot> items;
    private readonly HashSet<ulong> visitedCharacters;
    private readonly PlannerCapacitySnapshot capacity;

    public ulong MainCharacterId { get; }
    public ulong CurrentCharacterId { get; }
    public IReadOnlyList<InventoryItemSnapshot> Items => items;
    public IReadOnlyCollection<ulong> VisitedCharacters => visitedCharacters;
    public PlannerCapacitySnapshot Capacity => capacity;

    public PlannerState(
        ulong mainCharacterId,
        ulong currentCharacterId,
        IEnumerable<InventoryItemSnapshot> items,
        PlannerCapacitySnapshot capacity)
    {
        if (mainCharacterId == 0)
            throw new ArgumentOutOfRangeException(nameof(mainCharacterId));

        if (currentCharacterId == 0)
            throw new ArgumentOutOfRangeException(nameof(currentCharacterId));

        MainCharacterId = mainCharacterId;
        CurrentCharacterId = currentCharacterId;
        this.items = items.ToList();
        this.capacity = capacity;
        visitedCharacters = new HashSet<ulong>();

        if (currentCharacterId != mainCharacterId)
            visitedCharacters.Add(currentCharacterId);
    }

    private PlannerState(
        ulong mainCharacterId,
        ulong currentCharacterId,
        List<InventoryItemSnapshot> items,
        HashSet<ulong> visitedCharacters,
        PlannerCapacitySnapshot capacity)
    {
        MainCharacterId = mainCharacterId;
        CurrentCharacterId = currentCharacterId;
        this.items = items;
        this.visitedCharacters = visitedCharacters;
        this.capacity = capacity;
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
            updatedVisited,
            capacity);
    }

    public PlannerState WithItems(IEnumerable<InventoryItemSnapshot> newItems) =>
        new PlannerState(
            MainCharacterId,
            CurrentCharacterId,
            newItems.ToList(),
            visitedCharacters,
            capacity);

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

    public int GetMaximumMovableQuantity(
        InventorySource source,
        InventorySource destination,
        uint baseItemId,
        bool isHq,
        int requestedQuantity)
    {
        if (requestedQuantity <= 0)
            return 0;

        var sourceQuantity =
            GetQuantity(
                baseItemId,
                isHq,
                source);

        if (sourceQuantity <= 0)
            return 0;

        var destinationQuantity =
            capacity.GetAcceptableQuantity(
                destination,
                baseItemId,
                isHq,
                requestedQuantity);

        return Math.Min(
            sourceQuantity,
            destinationQuantity);
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

        if (GetMaximumMovableQuantity(
                source,
                destination,
                baseItemId,
                isHq,
                quantity) < quantity)
        {
            throw new InvalidOperationException(
                "The planner destination does not have enough logical capacity.");
        }

        if (!capacity.TryGetMaximumStack(
                baseItemId,
                out var maximumStack))
        {
            throw new InvalidOperationException(
                "The planner does not know the maximum stack size for this item.");
        }

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
        var sourceChanges = new List<SourceStackChange>();
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

            sourceChanges.Add(
                new SourceStackChange(
                    sourceItem,
                    newQuantity));

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

        var destinationRemaining = quantity;

        var nextVirtualSlot =
            updatedItems
                .Where(item =>
                    item.Storage == destination.Storage &&
                    item.OwnerId == destination.OwnerId &&
                    item.Slot < 0)
                .Select(item => item.Slot)
                .DefaultIfEmpty(0)
                .Min() - 1;

        var compatibleDestinationItems =
            updatedItems
                .Where(item =>
                    item.BaseItemId == baseItemId &&
                    item.IsHq == isHq &&
                    item.Storage == destination.Storage &&
                    item.OwnerId == destination.OwnerId &&
                    item.Quantity < maximumStack)
                .OrderBy(item => item.Container)
                .ThenBy(item => item.Slot)
                .ToList();

        foreach (var destinationItem in compatibleDestinationItems)
        {
            if (destinationRemaining == 0)
                break;

            var index =
                updatedItems.IndexOf(destinationItem);

            if (index < 0)
                continue;

            var merged =
                Math.Min(
                    destinationRemaining,
                    maximumStack - destinationItem.Quantity);

            if (merged <= 0)
                continue;

            updatedItems[index] =
                destinationItem with
                {
                    Quantity = destinationItem.Quantity + merged,
                    Container = destination.Container,
                    Slot = destinationItem.Container == destination.Container
                        ? destinationItem.Slot
                        : nextVirtualSlot--,
                    ParentCharacterId = destination.ParentCharacterId
                };

            destinationRemaining -= merged;
        }

        while (destinationRemaining > 0)
        {
            var stacked =
                Math.Min(
                    destinationRemaining,
                    maximumStack);

            updatedItems.Add(
                new InventoryItemSnapshot(
                    baseItemId,
                    rawItemId,
                    stacked,
                    isHq,
                    destination.Storage,
                    destination.OwnerId,
                    destination.Container,
                    nextVirtualSlot--,
                    DateTime.UtcNow,
                    false,
                    destination.ParentCharacterId));

            destinationRemaining -= stacked;
        }

        var updatedCapacity =
            capacity.ApplyMove(
                sourceChanges,
                destination,
                baseItemId,
                isHq,
                quantity);

        return new PlannerState(
            MainCharacterId,
            CurrentCharacterId,
            updatedItems,
            visitedCharacters,
            updatedCapacity);
    }
}
