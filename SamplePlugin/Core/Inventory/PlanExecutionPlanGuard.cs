using System;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

public sealed record PlanExecutionPlanValidityResult(
    bool IsValid,
    string Message)
{
    public static PlanExecutionPlanValidityResult Valid { get; } =
        new(
            true,
            string.Empty);
}

/// <summary>
/// Checks whether the remaining immutable planner actions are still physically
/// executable from the latest known InventoryIndex state.
///
/// This is deliberately not an InventoryIndex revision check: unrelated
/// inventory changes do not invalidate the plan. The guard replays only the
/// remaining planner actions through the same PlannerActionValidator used by
/// the planner.
/// </summary>
public sealed class PlanExecutionPlanGuard
{
    private readonly PlannerActionValidator actionValidator = new();

    public PlanExecutionPlanValidityResult ValidateRemaining(
        PlannerPlan plan,
        int firstActionIndex,
        ulong currentCharacterId,
        IReadOnlyList<InventoryItemSnapshot> currentItems)
    {
        if (firstActionIndex < 0)
            firstActionIndex = 0;

        var pendingActionIndices =
            Enumerable
                .Range(
                    firstActionIndex,
                    Math.Max(
                        0,
                        plan.Actions.Count -
                        firstActionIndex))
                .ToArray();

        return ValidateRemaining(
            plan,
            pendingActionIndices,
            currentCharacterId,
            currentItems);
    }

    public PlanExecutionPlanValidityResult ValidateRemaining(
        PlannerPlan plan,
        IReadOnlyCollection<int> pendingActionIndices,
        ulong currentCharacterId,
        IReadOnlyList<InventoryItemSnapshot> currentItems)
    {
        if (currentCharacterId == 0)
        {
            return new PlanExecutionPlanValidityResult(
                false,
                "Personaggio corrente non disponibile.");
        }

        var orderedIndices =
            pendingActionIndices
                .Where(index =>
                    index >= 0 &&
                    index < plan.Actions.Count)
                .Distinct()
                .OrderBy(index =>
                    index)
                .ToArray();

        if (orderedIndices.Length == 0)
            return PlanExecutionPlanValidityResult.Valid;

        var relevantItemIds =
            orderedIndices
                .Select(index =>
                    plan.Actions[index])
                .Where(action =>
                    action.Type == PlannerActionType.Move)
                .Select(action =>
                    action.BaseItemId)
                .ToHashSet();

        var plannerItems =
            currentItems
                .Where(item =>
                    relevantItemIds.Contains(
                        item.BaseItemId))
                .ToList();

        var state =
            new PlannerState(
                plan.InitialState.MainCharacterId,
                currentCharacterId,
                plannerItems,
                plan.InitialState.Capacity.Rebase(
                    currentItems));

        foreach (var index in orderedIndices)
        {
            var action =
                plan.Actions[index];

            if (action.Type == PlannerActionType.Move &&
                IsRelocatableExecutionSource(
                    action.Source))
            {
                if (!TryReplayRelocatableMove(
                        state,
                        action,
                        out var updatedState,
                        out var reason))
                {
                    return new PlanExecutionPlanValidityResult(
                        false,
                        $"Azione futura {index + 1}/{plan.Actions.Count} non più valida: {reason}");
                }

                state = updatedState;
                continue;
            }

            if (!actionValidator.CanApply(
                    state,
                    action,
                    out var validationReason))
            {
                return new PlanExecutionPlanValidityResult(
                    false,
                    $"Azione futura {index + 1}/{plan.Actions.Count} non più valida: {validationReason}");
            }

            state =
                action.Type switch
                {
                    PlannerActionType.SwitchCharacter =>
                        state.WithCurrentCharacter(
                            action.ToCharacterId),

                    PlannerActionType.Move =>
                        state.Move(
                            action.Source!,
                            action.Destination!,
                            action.BaseItemId,
                            action.IsHq,
                            action.Quantity),

                    _ =>
                        state
                };
        }

        return PlanExecutionPlanValidityResult.Valid;
    }

    private bool TryReplayRelocatableMove(
        PlannerState state,
        PlannerAction action,
        out PlannerState updatedState,
        out string reason)
    {
        updatedState = state;
        reason = string.Empty;

        if (action.Source is null ||
            action.Destination is null)
        {
            reason =
                "A move requires both a source and a destination.";
            return false;
        }

        var source =
            action.Source;

        var sourceContainers =
            state.Items
                .Where(item =>
                    item.BaseItemId == action.BaseItemId &&
                    item.IsHq == action.IsHq &&
                    item.Storage == source.Storage &&
                    item.OwnerId == source.OwnerId &&
                    (source.Storage != StorageType.Retainer ||
                     item.ParentCharacterId == source.ParentCharacterId) &&
                    item.Quantity > 0)
                .GroupBy(item =>
                    item.Container)
                .Select(group =>
                    new
                    {
                        Container = group.Key,
                        Quantity = group.Sum(item => item.Quantity)
                    })
                .OrderBy(group =>
                    group.Container == source.Container
                        ? 0
                        : 1)
                .ThenBy(group =>
                    group.Container)
                .ToList();

        if (sourceContainers.Sum(group => group.Quantity) <
            action.Quantity)
        {
            reason =
                "The source does not contain enough of the requested item.";
            return false;
        }

        var remaining =
            action.Quantity;

        foreach (var sourceContainer in sourceContainers)
        {
            if (remaining <= 0)
                break;

            var moved =
                Math.Min(
                    remaining,
                    sourceContainer.Quantity);

            if (moved <= 0)
                continue;

            var relocatedSource =
                source with
                {
                    Container = sourceContainer.Container
                };

            var replayAction =
                action with
                {
                    Source = relocatedSource,
                    Quantity = moved
                };

            if (!actionValidator.CanApply(
                    updatedState,
                    replayAction,
                    out reason))
            {
                return false;
            }

            updatedState =
                updatedState.Move(
                    relocatedSource,
                    action.Destination,
                    action.BaseItemId,
                    action.IsHq,
                    moved);

            remaining -= moved;
        }

        if (remaining == 0)
            return true;

        reason =
            "The source does not contain enough of the requested item.";
        return false;
    }

    private static bool IsRelocatableExecutionSource(
        InventorySource? source) =>
        source?.Storage == StorageType.CharacterInventory ||
        source?.Storage == StorageType.Retainer;
}
