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
        if (currentCharacterId == 0)
        {
            return new PlanExecutionPlanValidityResult(
                false,
                "Personaggio corrente non disponibile.");
        }

        if (firstActionIndex < 0)
            firstActionIndex = 0;

        if (firstActionIndex >= plan.Actions.Count)
            return PlanExecutionPlanValidityResult.Valid;

        var relevantItemIds =
            plan.Actions
                .Skip(firstActionIndex)
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
                plannerItems);

        for (var index = firstActionIndex;
             index < plan.Actions.Count;
             index++)
        {
            var action =
                plan.Actions[index];

            if (!actionValidator.CanApply(
                    state,
                    action,
                    out var reason))
            {
                return new PlanExecutionPlanValidityResult(
                    false,
                    $"Azione futura {index + 1}/{plan.Actions.Count} non più valida: {reason}");
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
}
