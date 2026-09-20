using CriticalCommonLib.Services;
using Dalamud.Game.Inventory;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

/// <summary>
/// Captures the game's visible inventory order on the framework thread.
/// The immutable snapshot can then be used safely by the background planner.
/// </summary>
public sealed class ExecutionOrderCompiler
{
    private const uint RetainerContainerFirst = 10000;
    private const uint RetainerContainerLast = 10006;

    private readonly IOdrScanner odrScanner;

    public ExecutionOrderCompiler(
        IOdrScanner odrScanner)
    {
        this.odrScanner = odrScanner;
    }

    public ExecutionOrderSnapshot Capture(
        IReadOnlyList<InventoryItemSnapshot> inventoryItems)
    {
        var displayIndices =
            new Dictionary<PhysicalStackKey, int>();

        foreach (var characterId in inventoryItems
                     .Where(item =>
                         item.Storage == StorageType.CharacterInventory)
                     .Select(item =>
                         item.OwnerId)
                     .Distinct())
        {
            CaptureCharacter(
                characterId,
                inventoryItems,
                displayIndices);
        }

        foreach (var retainerGroup in inventoryItems
                     .Where(item =>
                         item.Storage == StorageType.Retainer &&
                         item.ParentCharacterId != 0)
                     .GroupBy(item =>
                         new
                         {
                             item.OwnerId,
                             item.ParentCharacterId
                         }))
        {
            CaptureRetainer(
                retainerGroup.Key.OwnerId,
                retainerGroup.Key.ParentCharacterId,
                retainerGroup,
                displayIndices);
        }

        return new ExecutionOrderSnapshot(
            displayIndices);
    }

    public IReadOnlyList<InventoryItemSnapshot> OrderStacksForExecution(
        InventorySource source,
        IEnumerable<InventoryItemSnapshot> stacks)
    {
        var materialized =
            stacks.ToList();

        if (materialized.Count < 2)
            return materialized;

        var snapshot =
            Capture(
                materialized);

        return snapshot.OrderStacks(
            source,
            materialized);
    }

    private void CaptureCharacter(
        ulong characterId,
        IReadOnlyList<InventoryItemSnapshot> inventoryItems,
        Dictionary<PhysicalStackKey, int> result)
    {
        var sortOrder =
            odrScanner.GetSortOrder(
                characterId);

        if (sortOrder is null ||
            !sortOrder.NormalInventories.TryGetValue(
                "PlayerInventory",
                out var coordinates))
        {
            return;
        }

        foreach (var item in inventoryItems.Where(item =>
                     item.Storage == StorageType.CharacterInventory &&
                     item.OwnerId == characterId))
        {
            var containerIndex =
                GetCharacterContainerIndex(
                    item.Container);

            if (containerIndex < 0)
                continue;

            var displayIndex =
                FindDisplayIndex(
                    coordinates,
                    containerIndex,
                    item.Slot);

            if (displayIndex < 0)
                continue;

            result[
                PhysicalStackKey.From(
                    item)] =
                displayIndex;
        }
    }

    private void CaptureRetainer(
        ulong retainerId,
        ulong parentCharacterId,
        IEnumerable<InventoryItemSnapshot> inventoryItems,
        Dictionary<PhysicalStackKey, int> result)
    {
        var sortOrder =
            odrScanner.GetSortOrder(
                parentCharacterId);

        if (sortOrder is null ||
            !sortOrder.RetainerInventories.TryGetValue(
                retainerId,
                out var retainerSortOrder))
        {
            return;
        }

        var coordinates =
            retainerSortOrder.InventoryCoords;

        foreach (var item in inventoryItems)
        {
            if (item.Container < RetainerContainerFirst ||
                item.Container > RetainerContainerLast)
            {
                continue;
            }

            var containerIndex =
                checked(
                    (int)(item.Container -
                          RetainerContainerFirst));

            var displayIndex =
                FindDisplayIndex(
                    coordinates,
                    containerIndex,
                    item.Slot);

            if (displayIndex < 0)
                continue;

            result[
                PhysicalStackKey.From(
                    item)] =
                displayIndex;
        }
    }

    private static int FindDisplayIndex(
        IReadOnlyList<(int slotIndex, int containerIndex)> coordinates,
        int containerIndex,
        int slot)
    {
        for (var index = 0;
             index < coordinates.Count;
             index++)
        {
            var coordinate =
                coordinates[index];

            if (coordinate.containerIndex == containerIndex &&
                coordinate.slotIndex == slot)
            {
                return index;
            }
        }

        return -1;
    }

    private static int GetCharacterContainerIndex(
        uint container) =>
        container switch
        {
            (uint)GameInventoryType.Inventory1 => 0,
            (uint)GameInventoryType.Inventory2 => 1,
            (uint)GameInventoryType.Inventory3 => 2,
            (uint)GameInventoryType.Inventory4 => 3,
            _ => -1
        };
}

public sealed class ExecutionOrderSnapshot
{
    private readonly IReadOnlyDictionary<PhysicalStackKey, int>
        displayIndices;

    public ExecutionOrderSnapshot(
        IReadOnlyDictionary<PhysicalStackKey, int> displayIndices)
    {
        this.displayIndices =
            new Dictionary<PhysicalStackKey, int>(
                displayIndices);
    }

    public PlannerPlan Compile(
        PlannerPlan plan)
    {
        if (plan.Actions.Count < 2)
            return plan;

        var actions =
            plan.Actions.ToList();

        var start = 0;

        while (start < actions.Count)
        {
            if (actions[start].Type != PlannerActionType.Move)
            {
                start++;
                continue;
            }

            var end = start + 1;

            while (end < actions.Count &&
                   actions[end].Type == PlannerActionType.Move &&
                   IsSameExecutionGroup(
                       actions[start],
                       actions[end]))
            {
                end++;
            }

            if (end - start > 1)
            {
                var ordered =
                    actions
                        .GetRange(
                            start,
                            end - start)
                        .Select((action, originalIndex) =>
                            new OrderedAction(
                                action,
                                originalIndex,
                                GetActionOrder(
                                    plan,
                                    action)))
                        .OrderBy(entry =>
                            entry.Order.HasVisibleOrder
                                ? 0
                                : 1)
                        .ThenBy(entry =>
                            entry.Order.DisplayIndex)
                        .ThenBy(entry =>
                            entry.Order.PhysicalContainer)
                        .ThenBy(entry =>
                            entry.Order.PhysicalSlot)
                        .ThenBy(entry =>
                            entry.Action.BaseItemId)
                        .ThenBy(entry =>
                            entry.Action.IsHq)
                        .ThenBy(entry =>
                            entry.OriginalIndex)
                        .Select(entry =>
                            entry.Action)
                        .ToList();

                for (var index = 0;
                     index < ordered.Count;
                     index++)
                {
                    actions[start + index] =
                        ordered[index];
                }
            }

            start = end;
        }

        return plan.WithActions(
            actions);
    }

    public IReadOnlyList<InventoryItemSnapshot> OrderStacks(
        InventorySource source,
        IEnumerable<InventoryItemSnapshot> stacks)
    {
        return stacks
            .OrderBy(item =>
                TryGetDisplayIndex(
                    item,
                    out var displayIndex)
                    ? 0
                    : 1)
            .ThenBy(item =>
                TryGetDisplayIndex(
                    item,
                    out var displayIndex)
                    ? displayIndex
                    : int.MaxValue)
            .ThenBy(item =>
                item.Container)
            .ThenBy(item =>
                item.Slot)
            .ToList();
    }

    private ActionOrder GetActionOrder(
        PlannerPlan plan,
        PlannerAction action)
    {
        if (action.Source is null)
            return ActionOrder.Unavailable;

        var source =
            action.Source;

        var matchingStacks =
            plan.InitialState.Items
                .Where(item =>
                    item.Storage == source.Storage &&
                    item.OwnerId == source.OwnerId &&
                    item.Container == source.Container &&
                    item.BaseItemId == action.BaseItemId &&
                    item.IsHq == action.IsHq &&
                    item.Quantity > 0)
                .ToList();

        if (matchingStacks.Count == 0)
        {
            return new ActionOrder(
                false,
                int.MaxValue,
                source.Container,
                int.MaxValue);
        }

        var firstVisible =
            matchingStacks
                .Select(item =>
                    CreateStackOrder(
                        item))
                .OrderBy(entry =>
                    entry.HasVisibleOrder
                        ? 0
                        : 1)
                .ThenBy(entry =>
                    entry.DisplayIndex)
                .ThenBy(entry =>
                    entry.Item.Slot)
                .First();

        return new ActionOrder(
            firstVisible.HasVisibleOrder,
            firstVisible.DisplayIndex,
            source.Container,
            firstVisible.Item.Slot);
    }

    private StackOrder CreateStackOrder(
        InventoryItemSnapshot item)
    {
        var hasVisibleOrder =
            TryGetDisplayIndex(
                item,
                out var displayIndex);

        return new StackOrder(
            item,
            hasVisibleOrder,
            hasVisibleOrder
                ? displayIndex
                : int.MaxValue);
    }

    private bool TryGetDisplayIndex(
        InventoryItemSnapshot item,
        out int displayIndex) =>
        displayIndices.TryGetValue(
            PhysicalStackKey.From(
                item),
            out displayIndex);

    private static bool IsSameExecutionGroup(
        PlannerAction first,
        PlannerAction candidate)
    {
        if (first.Source is null ||
            first.Destination is null ||
            candidate.Source is null ||
            candidate.Destination is null)
        {
            return false;
        }

        return first.Source.Storage ==
                   candidate.Source.Storage &&
               first.Source.OwnerId ==
                   candidate.Source.OwnerId &&
               first.Source.ParentCharacterId ==
                   candidate.Source.ParentCharacterId &&
               first.Destination.Storage ==
                   candidate.Destination.Storage &&
               first.Destination.OwnerId ==
                   candidate.Destination.OwnerId;
    }

    private readonly record struct StackOrder(
        InventoryItemSnapshot Item,
        bool HasVisibleOrder,
        int DisplayIndex);

    private readonly record struct ActionOrder(
        bool HasVisibleOrder,
        int DisplayIndex,
        uint PhysicalContainer,
        int PhysicalSlot)
    {
        public static ActionOrder Unavailable { get; } =
            new(
                false,
                int.MaxValue,
                uint.MaxValue,
                int.MaxValue);
    }

    private readonly record struct OrderedAction(
        PlannerAction Action,
        int OriginalIndex,
        ActionOrder Order);
}

public readonly record struct PhysicalStackKey(
    StorageType Storage,
    ulong OwnerId,
    uint Container,
    int Slot)
{
    public static PhysicalStackKey From(
        InventoryItemSnapshot item) =>
        new(
            item.Storage,
            item.OwnerId,
            item.Container,
            item.Slot);
}
