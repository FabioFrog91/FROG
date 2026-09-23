using System;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

public readonly record struct PlanExecutionMaterializedAction(
    PlannerAction Action,
    IReadOnlyList<int> CoveredActionIndices);

/// <summary>
/// Builds the next logical execution action from the immutable planner plan.
/// Retainer and CharacterInventory MOVE actions are materialized across the
/// full commutable route group, so physical container/order changes do not
/// split one logical item transfer into stale planner chunks.
/// </summary>
public sealed class PlanExecutionMaterializer
{
    public PlanExecutionMaterializedAction Materialize(
        PlannerPlan plan,
        int firstActionIndex,
        IReadOnlyCollection<int>? completedActionIndices = null)
    {
        if (firstActionIndex < 0 ||
            firstActionIndex >= plan.Actions.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(firstActionIndex));
        }

        var completed =
            completedActionIndices is null
                ? new HashSet<int>()
                : completedActionIndices.ToHashSet();

        var first =
            plan.Actions[firstActionIndex];

        if (completed.Contains(
                firstActionIndex) ||
            !CanMaterializeAcrossContainers(
                first))
        {
            return new PlanExecutionMaterializedAction(
                first,
                new[] { firstActionIndex });
        }

        var groupEnd =
            FindExecutionGroupEnd(
                plan,
                firstActionIndex,
                first);

        var covered =
            new List<int>
            {
                firstActionIndex
            };

        var quantity =
            first.Quantity;

        for (var index = firstActionIndex + 1;
             index < groupEnd;
             index++)
        {
            if (completed.Contains(
                    index))
            {
                continue;
            }

            var candidate =
                plan.Actions[index];

            if (!IsSameLogicalMove(
                    first,
                    candidate))
            {
                continue;
            }

            quantity =
                checked(
                    quantity +
                    candidate.Quantity);

            covered.Add(
                index);
        }

        if (covered.Count == 1)
        {
            return new PlanExecutionMaterializedAction(
                first,
                covered);
        }

        return new PlanExecutionMaterializedAction(
            first with
            {
                Quantity = quantity
            },
            covered);
    }

    private static int FindExecutionGroupEnd(
        PlannerPlan plan,
        int startIndex,
        PlannerAction first)
    {
        var end =
            startIndex + 1;

        while (end < plan.Actions.Count)
        {
            var candidate =
                plan.Actions[end];

            if (candidate.Type != PlannerActionType.Move ||
                !IsSameExecutionGroup(
                    first,
                    candidate))
            {
                break;
            }

            end++;
        }

        return end;
    }

    private static bool CanMaterializeAcrossContainers(
        PlannerAction action) =>
        action.Type == PlannerActionType.Move &&
        action.Source is not null &&
        action.Destination is not null &&
        action.Quantity > 0 &&
        action.Source.Storage is
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

    private static bool IsSameLogicalMove(
        PlannerAction first,
        PlannerAction candidate) =>
        candidate.Type == PlannerActionType.Move &&
        candidate.Source is not null &&
        candidate.Destination is not null &&
        first.BaseItemId ==
            candidate.BaseItemId &&
        first.IsHq ==
            candidate.IsHq &&
        IsSameExecutionGroup(
            first,
            candidate);
}
