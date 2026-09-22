using Dalamud.Game.Inventory;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

public sealed record FreeCompanyDisplayPosition(
    int Page,
    int Slot,
    int Quantity);

public sealed record FreeCompanyDisplayLocation(
    bool IsAvailable,
    IReadOnlyList<FreeCompanyDisplayPosition> Positions)
{
    public static FreeCompanyDisplayLocation Unavailable { get; } =
        new(
            false,
            Array.Empty<FreeCompanyDisplayPosition>());
}

/// <summary>
/// Resolves a physical FC source action to its visible page, slots and
/// per-stack quantities. Page-agnostic FC destinations are deliberately not
/// resolved here: their physical page is learned from observation and replan.
/// </summary>
public sealed class FreeCompanyDisplayLocator
{
    private const uint FirstContainer =
        (uint)GameInventoryType.FreeCompanyPage1;

    private const uint LastContainer =
        (uint)GameInventoryType.FreeCompanyPage5;

    public FreeCompanyDisplayLocation LocateSource(
        PlannerPlan plan,
        int actionIndex,
        IReadOnlyList<InventoryItemSnapshot>? currentItems = null)
    {
        if (actionIndex < 0 ||
            actionIndex >= plan.Actions.Count)
        {
            return FreeCompanyDisplayLocation.Unavailable;
        }

        var action =
            plan.Actions[actionIndex];

        if (action.Type != PlannerActionType.Move ||
            action.Source is null ||
            action.Source.Storage != StorageType.FreeCompanyChest)
        {
            return FreeCompanyDisplayLocation.Unavailable;
        }

        var source =
            action.Source;

        if (source.Container < FirstContainer ||
            source.Container > LastContainer)
        {
            return FreeCompanyDisplayLocation.Unavailable;
        }

        var previouslyPlannedFromSameSource =
            currentItems is not null
                ? 0
                : plan.Actions
                .Take(actionIndex)
                .Where(previous =>
                    previous.Type == PlannerActionType.Move &&
                    previous.Source is not null &&
                    IsSamePhysicalSource(
                        previous.Source,
                        source) &&
                    previous.BaseItemId == action.BaseItemId &&
                    previous.IsHq == action.IsHq)
                .Sum(previous =>
                    previous.Quantity);

        var matchingStacks =
            (currentItems ?? plan.InitialState.Items)
                .Where(item =>
                    item.Storage == source.Storage &&
                    item.OwnerId == source.OwnerId &&
                    item.Container == source.Container &&
                    item.BaseItemId == action.BaseItemId &&
                    item.IsHq == action.IsHq &&
                    item.Quantity > 0)
                .OrderBy(item =>
                    item.Slot)
                .ToList();

        var page =
            checked(
                (int)(source.Container - FirstContainer) +
                1);

        var quantityToSkip =
            previouslyPlannedFromSameSource;

        var remaining =
            action.Quantity;

        var positions =
            new List<FreeCompanyDisplayPosition>();

        foreach (var stack in matchingStacks)
        {
            if (remaining <= 0)
                break;

            var available =
                stack.Quantity;

            if (quantityToSkip > 0)
            {
                var skipped =
                    Math.Min(
                        quantityToSkip,
                        available);

                quantityToSkip -= skipped;
                available -= skipped;
            }

            if (available <= 0)
                continue;

            var moved =
                Math.Min(
                    remaining,
                    available);

            positions.Add(
                new FreeCompanyDisplayPosition(
                    page,
                    stack.Slot + 1,
                    moved));

            remaining -= moved;
        }

        if (remaining > 0 ||
            positions.Count == 0)
        {
            return FreeCompanyDisplayLocation.Unavailable;
        }

        return new FreeCompanyDisplayLocation(
            true,
            positions);
    }

    private static bool IsSamePhysicalSource(
        InventorySource left,
        InventorySource right) =>
        left.Storage == right.Storage &&
        left.OwnerId == right.OwnerId &&
        left.Container == right.Container &&
        left.ParentCharacterId == right.ParentCharacterId;
}
