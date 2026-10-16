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
    private const int CharacterInventoryContainerCount = 4;
    private const int CharacterInventorySlotsPerContainer = 35;
    private const int RetainerInventoryContainerCount = 7;
    private const int RetainerInventorySlotsPerContainer = 25;

    private readonly IOdrScanner odrScanner;
    private readonly ExecutionOrderCatalog catalog;

    public ExecutionOrderCompiler(
        IOdrScanner odrScanner,
        ExecutionOrderCatalog catalog)
    {
        this.odrScanner = odrScanner;
        this.catalog = catalog;
    }

    public ExecutionOrderSnapshot Capture(
        IReadOnlyList<InventoryItemSnapshot> inventoryItems)
    {
        var displayPositions =
            new Dictionary<PhysicalStackKey, ExecutionOrderPosition>();

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
                displayPositions);
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
                displayPositions);
        }

        return new ExecutionOrderSnapshot(
            displayPositions);
    }

    public IReadOnlyList<InventoryItemSnapshot> OrderStacksForExecution(
        InventorySource source,
        IEnumerable<InventoryItemSnapshot> stacks) =>
        OrderStacksForExecution(
            stacks);

    public IReadOnlyList<InventoryItemSnapshot> OrderStacksForExecution(
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
            materialized);
    }

    private void CaptureCharacter(
        ulong characterId,
        IReadOnlyList<InventoryItemSnapshot> inventoryItems,
        Dictionary<PhysicalStackKey, ExecutionOrderPosition> result)
    {
        var items =
            inventoryItems.Where(item =>
                item.Storage == StorageType.CharacterInventory &&
                item.OwnerId == characterId);

        var sortOrder =
            odrScanner.GetSortOrder(
                characterId);

        if (sortOrder is not null &&
            sortOrder.NormalInventories.TryGetValue(
                "PlayerInventory",
                out var liveCoordinates) &&
            TryCreateCoordinates(
                liveCoordinates,
                CharacterInventoryContainerCount,
                CharacterInventorySlotsPerContainer,
                out var validLiveCoordinates))
        {
            CaptureCharacterCoordinates(
                items,
                validLiveCoordinates,
                ExecutionOrderDataSource.Live,
                result);

            return;
        }

        if (!catalog.TryGetCharacterInventory(
                characterId,
                out var persistedCoordinates))
        {
            return;
        }

        CaptureCharacterCoordinates(
            items,
            persistedCoordinates,
            ExecutionOrderDataSource.Persisted,
            result);
    }

    private void CaptureRetainer(
        ulong retainerId,
        ulong parentCharacterId,
        IEnumerable<InventoryItemSnapshot> inventoryItems,
        Dictionary<PhysicalStackKey, ExecutionOrderPosition> result)
    {
        var sortOrder =
            odrScanner.GetSortOrder(
                parentCharacterId);

        if (sortOrder is not null &&
            sortOrder.RetainerInventories.TryGetValue(
                retainerId,
                out var retainerSortOrder) &&
            TryCreateCoordinates(
                retainerSortOrder.InventoryCoords,
                RetainerInventoryContainerCount,
                RetainerInventorySlotsPerContainer,
                out var validLiveCoordinates))
        {
            CaptureRetainerCoordinates(
                inventoryItems,
                validLiveCoordinates,
                ExecutionOrderDataSource.Live,
                result);

            return;
        }

        if (!catalog.TryGetRetainer(
                parentCharacterId,
                retainerId,
                out var persistedCoordinates))
        {
            return;
        }

        CaptureRetainerCoordinates(
            inventoryItems,
            persistedCoordinates,
            ExecutionOrderDataSource.Persisted,
            result);
    }

    private static void CaptureCharacterCoordinates(
        IEnumerable<InventoryItemSnapshot> inventoryItems,
        IReadOnlyList<ExecutionOrderCoordinate> coordinates,
        ExecutionOrderDataSource source,
        Dictionary<PhysicalStackKey, ExecutionOrderPosition> result)
    {
        foreach (var item in inventoryItems)
        {
            var containerIndex =
                GetCharacterContainerIndex(
                    item.Container);

            if (containerIndex < 0)
                continue;

            AddDisplayPosition(
                item,
                coordinates,
                containerIndex,
                source,
                result);
        }
    }

    private static void CaptureRetainerCoordinates(
        IEnumerable<InventoryItemSnapshot> inventoryItems,
        IReadOnlyList<ExecutionOrderCoordinate> coordinates,
        ExecutionOrderDataSource source,
        Dictionary<PhysicalStackKey, ExecutionOrderPosition> result)
    {
        foreach (var item in inventoryItems)
        {
            if (item.Container < RetainerContainerFirst ||
                item.Container > RetainerContainerLast)
            {
                continue;
            }

            AddDisplayPosition(
                item,
                coordinates,
                checked(
                    (int)(item.Container -
                          RetainerContainerFirst)),
                source,
                result);
        }
    }

    private static void AddDisplayPosition(
        InventoryItemSnapshot item,
        IReadOnlyList<ExecutionOrderCoordinate> coordinates,
        int containerIndex,
        ExecutionOrderDataSource source,
        Dictionary<PhysicalStackKey, ExecutionOrderPosition> result)
    {
        var displayIndex =
            FindDisplayIndex(
                coordinates,
                containerIndex,
                item.Slot);

        if (displayIndex < 0)
            return;

        result[
            PhysicalStackKey.From(
                item)] =
            new ExecutionOrderPosition(
                displayIndex,
                source);
    }

    private static int FindDisplayIndex(
        IReadOnlyList<ExecutionOrderCoordinate> coordinates,
        int containerIndex,
        int slot)
    {
        for (var index = 0;
             index < coordinates.Count;
             index++)
        {
            var coordinate =
                coordinates[index];

            if (coordinate.ContainerIndex == containerIndex &&
                coordinate.SlotIndex == slot)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool TryCreateCoordinates(
        IReadOnlyList<(int slotIndex, int containerIndex)> coordinates,
        int containerCount,
        int slotsPerContainer,
        out IReadOnlyList<ExecutionOrderCoordinate> result)
    {
        if (coordinates.Count !=
            containerCount *
            slotsPerContainer)
        {
            result =
                Array.Empty<ExecutionOrderCoordinate>();

            return false;
        }

        var captured =
            coordinates
                .Select(coordinate =>
                    new ExecutionOrderCoordinate(
                        coordinate.slotIndex,
                        coordinate.containerIndex))
                .ToList();

        if (captured.Any(coordinate =>
                coordinate.ContainerIndex < 0 ||
                coordinate.ContainerIndex >= containerCount ||
                coordinate.SlotIndex < 0 ||
                coordinate.SlotIndex >= slotsPerContainer) ||
            captured.Distinct().Count() != captured.Count)
        {
            result =
                Array.Empty<ExecutionOrderCoordinate>();

            return false;
        }

        result = captured;
        return true;
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
    private readonly IReadOnlyDictionary<PhysicalStackKey, ExecutionOrderPosition>
        displayPositions;

    public ExecutionOrderSnapshot(
        IReadOnlyDictionary<PhysicalStackKey, ExecutionOrderPosition> displayPositions)
    {
        this.displayPositions =
            new Dictionary<PhysicalStackKey, ExecutionOrderPosition>(
                displayPositions);
    }

    public PlannerPlan Compile(
        PlannerPlan plan)
    {
        if (plan.Actions.Count < 2)
            return plan;

        var actions =
            plan.Actions.ToList();

        var decisionActions =
            plan.Actions.ToList();

        var actionsChanged = false;
        var decisionOrderChanged = false;

        var consumedBySource =
            new Dictionary<ActionConsumptionKey, int>();

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
                var consumedInsideGroup =
                    new Dictionary<ActionConsumptionKey, int>();

                var candidates =
                    actions
                        .GetRange(
                            start,
                            end - start)
                        .Select((action, originalIndex) =>
                        {
                            var key =
                                ActionConsumptionKey.From(
                                    action);

                            var quantityToSkip =
                                GetConsumedQuantity(
                                    consumedBySource,
                                    key) +
                                GetConsumedQuantity(
                                    consumedInsideGroup,
                                    key);

                            AddConsumedQuantity(
                                consumedInsideGroup,
                                key,
                                action.Quantity);

                            return new OrderedAction(
                                action,
                                originalIndex,
                                GetActionOrder(
                                    plan,
                                    action,
                                    quantityToSkip));
                        })
                        .ToList();

                var hasCompleteVisibleOrder =
                    candidates.All(entry =>
                        entry.Order.HasVisibleOrder);

                var orderedEntries =
                    (hasCompleteVisibleOrder
                        ? candidates
                            .OrderBy(entry =>
                                entry.Order.DisplayIndex)
                            .ThenBy(entry =>
                                entry.Order.PhysicalContainer)
                            .ThenBy(entry =>
                                entry.Order.PhysicalSlot)
                        : candidates
                            .OrderBy(entry =>
                                entry.Order.PhysicalContainer)
                            .ThenBy(entry =>
                                entry.Order.PhysicalSlot))
                    .ThenBy(entry =>
                        entry.Action.BaseItemId)
                    .ThenBy(entry =>
                        entry.Action.IsHq)
                    .ThenBy(entry =>
                        entry.OriginalIndex)
                    .ToList();

                RecordActionOrder(
                    actions[start],
                    start,
                    end,
                    hasCompleteVisibleOrder,
                    candidates,
                    orderedEntries);

                for (var index = 0;
                     index < orderedEntries.Count;
                     index++)
                {
                    var orderedAction =
                        orderedEntries[index].Action;

                    if (!Equals(
                            actions[start + index],
                            orderedAction))
                    {
                        actionsChanged = true;
                    }

                    actions[start + index] =
                        orderedAction;

                    if (hasCompleteVisibleOrder &&
                        IsSupportedExecutionOrderSource(
                            orderedAction) &&
                        !Equals(
                            decisionActions[start + index],
                            orderedAction))
                    {
                        decisionOrderChanged = true;
                        decisionActions[start + index] =
                            orderedAction;
                    }
                }
            }

            for (var index = start;
                 index < end;
                 index++)
            {
                var action =
                    actions[index];

                AddConsumedQuantity(
                    consumedBySource,
                    ActionConsumptionKey.From(
                        action),
                    action.Quantity);
            }

            start = end;
        }

        if (decisionOrderChanged)
        {
            return plan.WithExecutionOrdering(
                actions,
                decisionActions);
        }

        return actionsChanged
            ? plan.WithActions(
                actions)
            : plan;
    }

    public IReadOnlyList<InventoryItemSnapshot> OrderStacks(
        IEnumerable<InventoryItemSnapshot> stacks)
    {
        var materialized =
            stacks.ToList();

        var hasCompleteVisibleOrder =
            materialized.Count > 0 &&
            materialized.All(item =>
                TryGetDisplayIndex(
                    item,
                    out _));

        var ordered =
            hasCompleteVisibleOrder
                ? materialized
                    .OrderBy(item =>
                        displayPositions[
                            PhysicalStackKey.From(
                                item)].DisplayIndex)
                    .ThenBy(item =>
                        item.Container)
                    .ThenBy(item =>
                        item.Slot)
                    .ToList()
                : materialized
                    .OrderBy(item =>
                        item.Container)
                    .ThenBy(item =>
                        item.Slot)
                    .ToList();

        RecordStackOrder(
            materialized,
            ordered,
            hasCompleteVisibleOrder);

        return ordered;
    }

    private void RecordActionOrder(
        PlannerAction groupStart,
        int start,
        int end,
        bool hasCompleteVisibleOrder,
        IReadOnlyList<OrderedAction> candidates,
        IReadOnlyList<OrderedAction> ordered)
    {
        if (!ForensicTraceRecorder.IsActive ||
            groupStart.Source is null ||
            groupStart.Destination is null)
        {
            return;
        }

        var key =
            $"PLAN:{groupStart.Source.Storage}:{groupStart.Source.OwnerId}:{groupStart.Source.ParentCharacterId}>" +
            $"{groupStart.Destination.Storage}:{groupStart.Destination.OwnerId}:{groupStart.Destination.ParentCharacterId}:{start}:{end}";

        var candidateText =
            string.Join(
                " || ",
                candidates.Select(entry =>
                    $"item={entry.Action.BaseItemId} hq={entry.Action.IsHq} qty={entry.Action.Quantity} " +
                    $"rawContainer={entry.Action.Source?.Container} rawSlot={entry.Order.PhysicalSlot} " +
                    $"odrFound={entry.Order.HasVisibleOrder} odrDisplayIndex={(entry.Order.HasVisibleOrder ? entry.Order.DisplayIndex : -1)} odrSource={entry.Order.DataSource} " +
                    $"originalIndex={entry.OriginalIndex}"));

        var orderedText =
            string.Join(
                " -> ",
                ordered.Select(entry =>
                    $"{entry.Action.BaseItemId}{(entry.Action.IsHq ? "HQ" : "NQ")}" +
                    $"[raw={entry.Action.Source?.Container}:{entry.Order.PhysicalSlot},odr={(entry.Order.HasVisibleOrder ? entry.Order.DisplayIndex : -1)},source={entry.Order.DataSource}]"));

        ForensicTraceRecorder.RecordState(
            "EXECUTION_ORDER",
            key,
            $"ACTION_GROUP source={groupStart.Source.Storage}/owner={groupStart.Source.OwnerId}/parent={groupStart.Source.ParentCharacterId} " +
            $"destination={groupStart.Destination.Storage}/owner={groupStart.Destination.OwnerId}/parent={groupStart.Destination.ParentCharacterId} " +
            $"range={start}-{end - 1} mode={DescribeActionMode(hasCompleteVisibleOrder, candidates)} candidates={candidateText} selected={orderedText}");
    }

    private void RecordStackOrder(
        IReadOnlyList<InventoryItemSnapshot> input,
        IReadOnlyList<InventoryItemSnapshot> ordered,
        bool hasCompleteVisibleOrder)
    {
        if (!ForensicTraceRecorder.IsActive ||
            input.Count == 0)
        {
            return;
        }

        var first = input[0];
        var key =
            $"STACKS:{first.Storage}:{first.OwnerId}:{first.ParentCharacterId}:{first.BaseItemId}:{first.IsHq}";

        var inputText =
            string.Join(
                " || ",
                input.Select(item =>
                {
                    var found =
                        TryGetDisplayIndex(
                            item,
                            out var displayIndex);

                    return
                        $"raw={item.Container}:{item.Slot} qty={item.Quantity} odrFound={found} odrDisplayIndex={(found ? displayIndex : -1)} odrSource={GetDisplaySource(item)}";
                }));

        var orderedText =
            string.Join(
                " -> ",
                ordered.Select(item =>
                {
                    var found =
                        TryGetDisplayIndex(
                            item,
                            out var displayIndex);

                    return
                        $"{item.Container}:{item.Slot}[qty={item.Quantity},odr={(found ? displayIndex : -1)},source={GetDisplaySource(item)}]";
                }));

        ForensicTraceRecorder.RecordState(
            "EXECUTION_ORDER",
            key,
            $"STACK_ORDER item={first.BaseItemId} hq={first.IsHq} storage={first.Storage} owner={first.OwnerId} parent={first.ParentCharacterId} " +
            $"mode={DescribeStackMode(hasCompleteVisibleOrder, input)} input={inputText} selected={orderedText}");
    }

    private static string DescribeActionMode(
        bool hasCompleteVisibleOrder,
        IReadOnlyList<OrderedAction> candidates)
    {
        if (!hasCompleteVisibleOrder)
            return "RAW_FALLBACK";

        var sources =
            candidates
                .Select(entry =>
                    entry.Order.DataSource)
                .Distinct()
                .ToArray();

        return DescribeSources(
            sources);
    }

    private string DescribeStackMode(
        bool hasCompleteVisibleOrder,
        IReadOnlyList<InventoryItemSnapshot> stacks)
    {
        if (!hasCompleteVisibleOrder)
            return "RAW_FALLBACK";

        var sources =
            stacks
                .Select(GetDisplaySource)
                .Distinct()
                .ToArray();

        return DescribeSources(
            sources);
    }

    private static string DescribeSources(
        IReadOnlyList<ExecutionOrderDataSource> sources) =>
        sources.Count == 1
            ? sources[0] switch
            {
                ExecutionOrderDataSource.Live => "ODR_LIVE",
                ExecutionOrderDataSource.Persisted => "ODR_PERSISTED",
                _ => "RAW_FALLBACK"
            }
            : "ODR_MIXED";

    private ActionOrder GetActionOrder(
        PlannerPlan plan,
        PlannerAction action,
        int quantityToSkip)
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
                int.MaxValue,
                ExecutionOrderDataSource.None);
        }

        var hasCompleteVisibleOrder =
            matchingStacks.All(item =>
                TryGetDisplayIndex(
                    item,
                    out _));

        var orderedStacks =
            hasCompleteVisibleOrder
                ? matchingStacks
                    .OrderBy(item =>
                        displayPositions[
                            PhysicalStackKey.From(
                                item)].DisplayIndex)
                    .ThenBy(item =>
                        item.Slot)
                : matchingStacks
                    .OrderBy(item =>
                        item.Container)
                    .ThenBy(item =>
                        item.Slot);

        InventoryItemSnapshot? firstConsumedStack = null;

        foreach (var stack in orderedStacks)
        {
            if (quantityToSkip >= stack.Quantity)
            {
                quantityToSkip -=
                    stack.Quantity;

                continue;
            }

            firstConsumedStack =
                stack;

            break;
        }

        if (firstConsumedStack is null)
        {
            return new ActionOrder(
                false,
                int.MaxValue,
                source.Container,
                int.MaxValue,
                ExecutionOrderDataSource.None);
        }

        var displayIndex =
            int.MaxValue;

        var hasVisibleOrder =
            hasCompleteVisibleOrder &&
            TryGetDisplayIndex(
                firstConsumedStack,
                out displayIndex);

        return new ActionOrder(
            hasVisibleOrder,
            hasVisibleOrder
                ? displayIndex
                : int.MaxValue,
            source.Container,
            firstConsumedStack.Slot,
            hasVisibleOrder
                ? GetDisplaySource(
                    firstConsumedStack)
                : ExecutionOrderDataSource.None);
    }

    private static int GetConsumedQuantity(
        Dictionary<ActionConsumptionKey, int> consumed,
        ActionConsumptionKey key) =>
        consumed.TryGetValue(
            key,
            out var quantity)
            ? quantity
            : 0;

    private static void AddConsumedQuantity(
        Dictionary<ActionConsumptionKey, int> consumed,
        ActionConsumptionKey key,
        int quantity)
    {
        consumed[key] =
            GetConsumedQuantity(
                consumed,
                key) +
            quantity;
    }

    private bool TryGetDisplayIndex(
        InventoryItemSnapshot item,
        out int displayIndex)
    {
        if (displayPositions.TryGetValue(
                PhysicalStackKey.From(
                    item),
                out var position))
        {
            displayIndex =
                position.DisplayIndex;

            return true;
        }

        displayIndex =
            int.MaxValue;

        return false;
    }

    private ExecutionOrderDataSource GetDisplaySource(
        InventoryItemSnapshot item) =>
        displayPositions.TryGetValue(
            PhysicalStackKey.From(
                item),
            out var position)
                ? position.Source
                : ExecutionOrderDataSource.None;

    private static bool IsSupportedExecutionOrderSource(
        PlannerAction action) =>
        action.Source?.Storage is
            StorageType.Retainer or
            StorageType.CharacterInventory;

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

        // Source.Container is intentionally not compared: physical pages of
        // the same inventory are precisely what this post-compiler orders.
        // The storage owners and route layer must still match, so no move is
        // allowed to cross a switch or a Retainer -> Inventory -> FC boundary.
        return first.Source.Storage ==
                   candidate.Source.Storage &&
               first.Source.OwnerId ==
                   candidate.Source.OwnerId &&
               first.Source.ParentCharacterId ==
                   candidate.Source.ParentCharacterId &&
               first.Destination.Storage ==
                   candidate.Destination.Storage &&
               first.Destination.OwnerId ==
                   candidate.Destination.OwnerId &&
               first.Destination.ParentCharacterId ==
                   candidate.Destination.ParentCharacterId;
    }

    private readonly record struct ActionConsumptionKey(
        StorageType Storage,
        ulong OwnerId,
        uint Container,
        ulong ParentCharacterId,
        uint BaseItemId,
        bool IsHq)
    {
        public static ActionConsumptionKey From(
            PlannerAction action) =>
            new(
                action.Source!.Storage,
                action.Source.OwnerId,
                action.Source.Container,
                action.Source.ParentCharacterId,
                action.BaseItemId,
                action.IsHq);
    }

    private readonly record struct ActionOrder(
        bool HasVisibleOrder,
        int DisplayIndex,
        uint PhysicalContainer,
        int PhysicalSlot,
        ExecutionOrderDataSource DataSource)
    {
        public static ActionOrder Unavailable { get; } =
            new(
                false,
                int.MaxValue,
                uint.MaxValue,
                int.MaxValue,
                ExecutionOrderDataSource.None);
    }

    private readonly record struct OrderedAction(
        PlannerAction Action,
        int OriginalIndex,
        ActionOrder Order);
}

public enum ExecutionOrderDataSource
{
    None,
    Live,
    Persisted
}

public readonly record struct ExecutionOrderPosition(
    int DisplayIndex,
    ExecutionOrderDataSource Source);

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
