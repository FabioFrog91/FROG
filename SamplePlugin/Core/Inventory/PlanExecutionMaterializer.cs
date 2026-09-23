using System;

namespace FROG.Core.Inventory;

public readonly record struct PlanExecutionMaterializedAction(
    PlannerAction Action,
    int CoveredActionCount);

/// <summary>
/// Collapses consecutive planner MOVE actions that represent the same logical
/// transfer while ignoring only the physical source container for storages
/// whose execution semantics are logical across their internal containers.
/// </summary>
public sealed class PlanExecutionMaterializer
{
    public PlanExecutionMaterializedAction Materialize(
        PlannerPlan plan,
        int firstActionIndex)
    {
        if (firstActionIndex < 0 ||
            firstActionIndex >= plan.Actions.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(firstActionIndex));
        }

        var first =
            plan.Actions[firstActionIndex];

        if (!CanMaterializeAcrossContainers(
                first))
        {
            return new PlanExecutionMaterializedAction(
                first,
                1);
        }

        var quantity =
            first.Quantity;

        var covered =
            1;

        for (var index = firstActionIndex + 1;
             index < plan.Actions.Count;
             index++)
        {
            var candidate =
                plan.Actions[index];

            if (!IsSameLogicalMove(
                    first,
                    candidate))
            {
                break;
            }

            quantity =
                checked(
                    quantity +
                    candidate.Quantity);

            covered++;
        }

        if (covered == 1)
        {
            return new PlanExecutionMaterializedAction(
                first,
                1);
        }

        return new PlanExecutionMaterializedAction(
            first with
            {
                Quantity = quantity
            },
            covered);
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

    private static bool IsSameLogicalMove(
        PlannerAction first,
        PlannerAction candidate)
    {
        if (!CanMaterializeAcrossContainers(
                candidate) ||
            first.Source is null ||
            first.Destination is null ||
            candidate.Source is null ||
            candidate.Destination is null)
        {
            return false;
        }

        return first.BaseItemId ==
                   candidate.BaseItemId &&
               first.IsHq ==
                   candidate.IsHq &&
               first.Source.Storage ==
                   candidate.Source.Storage &&
               first.Source.OwnerId ==
                   candidate.Source.OwnerId &&
               first.Source.ParentCharacterId ==
                   candidate.Source.ParentCharacterId &&
               first.Destination.Storage ==
                   candidate.Destination.Storage &&
               first.Destination.OwnerId ==
                   candidate.Destination.OwnerId &&
               first.Destination.Container ==
                   candidate.Destination.Container &&
               first.Destination.ParentCharacterId ==
                   candidate.Destination.ParentCharacterId;
    }
}
