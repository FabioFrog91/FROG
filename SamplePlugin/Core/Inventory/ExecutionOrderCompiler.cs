using CriticalCommonLib.Services;
using Dalamud.Game.Inventory;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

/// <summary>
/// Reorders already-selected MOVE actions into the order the player actually
/// sees in game. This runs after optimization, so it cannot change source
/// selection, quantities, scoring, missing counts, or search complexity.
///
/// Only contiguous MOVE runs are reordered. SWITCH boundaries and route phases
/// remain intact. When ODR data is unavailable, the original deterministic
/// physical container/slot ordering is used.
/// </summary>
public sealed class ExecutionOrderCompiler
{
    private const uint RetainerContainerFirst = 10000;
    private const uint RetainerContainerLast = 10006;
    private const int RetainerPhysicalSlotsPerContainer = 25;

    private readonly IOdrScanner odrScanner;

    public ExecutionOrderCompiler(
        IOdrScanner odrScanner)
    {
        this.odrScanner = odrScanner;
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
                   IsSameExecutionPhase(
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
                            entry.Order.IsDisplayOrderAvailable
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

        if (source.Storage == StorageType.Retainer)
        {
            var visible =
                TryGetRetainerDisplayIndices(
                    source,
                    matchingStacks);

            if (visible.Count > 0)
            {
                return new ActionOrder(
                    true,
                    visible.Min(pair =>
                        pair.DisplayIndex),
                    source.Container,
                    visible
                        .OrderBy(pair =>
                            pair.DisplayIndex)
                        .First()
                        .PhysicalSlot);
            }
        }
        else if (source.Storage ==
                 StorageType.CharacterInventory)
        {
            var visible =
                TryGetCharacterDisplayIndices(
                    source,
                    matchingStacks);

            if (visible.Count > 0)
            {
                return new ActionOrder(
                    true,
                    visible.Min(pair =>
                        pair.DisplayIndex),
                    source.Container,
                    visible
                        .OrderBy(pair =>
                            pair.DisplayIndex)
                        .First()
                        .PhysicalSlot);
            }
        }

        return new ActionOrder(
            false,
            int.MaxValue,
            source.Container,
            matchingStacks.Min(item =>
                item.Slot));
    }

    public IReadOnlyList<InventoryItemSnapshot> OrderStacksForExecution(
        InventorySource source,
        IEnumerable<InventoryItemSnapshot> stacks)
    {
        var materialized =
            stacks.ToList();

        if (materialized.Count < 2)
            return materialized;

        if (source.Storage == StorageType.Retainer)
        {
            var display =
                TryGetRetainerDisplayIndices(
                    source,
                    materialized);

            if (display.Count == materialized.Count)
            {
                var indexBySlot =
                    display.ToDictionary(
                        pair => pair.PhysicalSlot,
                        pair => pair.DisplayIndex);

                return materialized
                    .OrderBy(item =>
                        indexBySlot[item.Slot])
                    .ThenBy(item =>
                        item.Slot)
                    .ToList();
            }
        }

        if (source.Storage == StorageType.CharacterInventory)
        {
            var display =
                TryGetCharacterDisplayIndices(
                    source,
                    materialized);

            if (display.Count == materialized.Count)
            {
                var indexBySlot =
                    display.ToDictionary(
                        pair => pair.PhysicalSlot,
                        pair => pair.DisplayIndex);

                return materialized
                    .OrderBy(item =>
                        indexBySlot[item.Slot])
                    .ThenBy(item =>
                        item.Slot)
                    .ToList();
            }
        }

        return materialized
            .OrderBy(item =>
                item.Slot)
            .ToList();
    }

    private List<DisplayStackIndex> TryGetRetainerDisplayIndices(
        InventorySource source,
        IReadOnlyList<InventoryItemSnapshot> stacks)
    {
        var result =
            new List<DisplayStackIndex>();

        if (source.Container < RetainerContainerFirst ||
            source.Container > RetainerContainerLast ||
            source.ParentCharacterId == 0)
        {
            return result;
        }

        var sortOrder =
            odrScanner.GetSortOrder(
                source.ParentCharacterId);

        if (sortOrder is null ||
            !sortOrder.RetainerInventories.TryGetValue(
                source.OwnerId,
                out var retainerSortOrder))
        {
            return result;
        }

        var coordinates =
            retainerSortOrder.InventoryCoords;

        var containerIndex =
            checked(
                (int)(source.Container -
                      RetainerContainerFirst));

        foreach (var stack in stacks)
        {
            var displayIndex =
                FindDisplayIndex(
                    coordinates,
                    containerIndex,
                    stack.Slot);

            if (displayIndex >= 0)
            {
                result.Add(
                    new DisplayStackIndex(
                        stack.Slot,
                        displayIndex));
            }
        }

        return result;
    }

    private List<DisplayStackIndex> TryGetCharacterDisplayIndices(
        InventorySource source,
        IReadOnlyList<InventoryItemSnapshot> stacks)
    {
        var result =
            new List<DisplayStackIndex>();

        var containerIndex =
            GetCharacterContainerIndex(
                source.Container);

        if (containerIndex < 0)
            return result;

        var sortOrder =
            odrScanner.GetSortOrder(
                source.OwnerId);

        if (sortOrder is null ||
            !sortOrder.NormalInventories.TryGetValue(
                "PlayerInventory",
                out var coordinates))
        {
            return result;
        }

        foreach (var stack in stacks)
        {
            var displayIndex =
                FindDisplayIndex(
                    coordinates,
                    containerIndex,
                    stack.Slot);

            if (displayIndex >= 0)
            {
                result.Add(
                    new DisplayStackIndex(
                        stack.Slot,
                        displayIndex));
            }
        }

        return result;
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

    private static bool IsSameExecutionPhase(
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
               GetExecutionOwner(first.Source) ==
                   GetExecutionOwner(candidate.Source) &&
               first.Destination.Storage ==
                   candidate.Destination.Storage &&
               first.Destination.OwnerId ==
                   candidate.Destination.OwnerId;
    }

    private static ulong GetExecutionOwner(
        InventorySource source) =>
        source.Storage == StorageType.Retainer
            ? source.ParentCharacterId
            : source.OwnerId;

    private readonly record struct DisplayStackIndex(
        int PhysicalSlot,
        int DisplayIndex);

    private readonly record struct ActionOrder(
        bool IsDisplayOrderAvailable,
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
