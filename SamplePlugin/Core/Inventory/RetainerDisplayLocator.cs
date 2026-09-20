using CriticalCommonLib.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

public sealed record RetainerDisplayPosition(
    int Page,
    int Slot,
    int Quantity);

public sealed record RetainerDisplayLocation(
    bool IsAvailable,
    IReadOnlyList<RetainerDisplayPosition> Positions)
{
    public static RetainerDisplayLocation Unavailable { get; } =
        new(
            false,
            Array.Empty<RetainerDisplayPosition>());
}

/// <summary>
/// Translates FFXIV's physical retainer storage (7 containers x 25 slots)
/// into the user-visible retainer inventory (5 pages x 35 slots).
/// Planner and verifier keep using the physical coordinates; this class is
/// presentation-only and uses the game's ODR sort order for the owner.
/// </summary>
public sealed class RetainerDisplayLocator
{
    private const uint RetainerContainerFirst = 10000;
    private const uint RetainerContainerLast = 10006;
    private const int PhysicalSlotsPerContainer = 25;
    private const int VisibleSlotsPerPage = 35;

    private readonly IOdrScanner odrScanner;

    public RetainerDisplayLocator(
        IOdrScanner odrScanner)
    {
        this.odrScanner = odrScanner;
    }

    public RetainerDisplayLocation LocateSource(
        PlannerPlan plan,
        int actionIndex)
    {
        if (actionIndex < 0 ||
            actionIndex >= plan.Actions.Count)
        {
            return RetainerDisplayLocation.Unavailable;
        }

        var action =
            plan.Actions[actionIndex];

        if (action.Type != PlannerActionType.Move ||
            action.Source is null ||
            action.Source.Storage != StorageType.Retainer)
        {
            return RetainerDisplayLocation.Unavailable;
        }

        var source =
            action.Source;

        if (source.Container < RetainerContainerFirst ||
            source.Container > RetainerContainerLast ||
            source.ParentCharacterId == 0)
        {
            return RetainerDisplayLocation.Unavailable;
        }

        var sortOrder =
            odrScanner.GetSortOrder(
                source.ParentCharacterId);

        if (sortOrder is null ||
            !sortOrder.RetainerInventories.TryGetValue(
                source.OwnerId,
                out var retainerSortOrder))
        {
            return RetainerDisplayLocation.Unavailable;
        }

        var coordinates =
            retainerSortOrder.InventoryCoords;

        if (coordinates.Count == 0)
            return RetainerDisplayLocation.Unavailable;

        var previouslyPlannedFromSameSource =
            plan.Actions
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

        var physicalContainerIndex =
            checked(
                (int)(source.Container -
                      RetainerContainerFirst));

        var matchingStacks =
            plan.InitialState.Items
                .Where(item =>
                    item.Storage == source.Storage &&
                    item.OwnerId == source.OwnerId &&
                    item.Container == source.Container &&
                    item.BaseItemId == action.BaseItemId &&
                    item.IsHq == action.IsHq &&
                    item.Quantity > 0)
                .OrderBy(item =>
                {
                    var displayIndex =
                        FindDisplayIndex(
                            coordinates,
                            physicalContainerIndex,
                            item.Slot);

                    return displayIndex >= 0
                        ? displayIndex
                        : int.MaxValue;
                })
                .ThenBy(item =>
                    item.Slot)
                .ToList();

        var remaining =
            action.Quantity;

        var quantityToSkip =
            previouslyPlannedFromSameSource;

        var positions =
            new List<RetainerDisplayPosition>();

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

                quantityToSkip -=
                    skipped;

                available -=
                    skipped;
            }

            if (available <= 0)
                continue;

            var moved =
                Math.Min(
                    remaining,
                    available);

            var displayIndex =
                FindDisplayIndex(
                    coordinates,
                    physicalContainerIndex,
                    stack.Slot);

            if (displayIndex < 0)
            {
                return RetainerDisplayLocation.Unavailable;
            }

            positions.Add(
                new RetainerDisplayPosition(
                    Page:
                        displayIndex /
                        VisibleSlotsPerPage +
                        1,
                    Slot:
                        displayIndex %
                        VisibleSlotsPerPage +
                        1,
                    Quantity:
                        moved));

            remaining -= moved;
        }

        if (remaining > 0 ||
            positions.Count == 0)
        {
            return RetainerDisplayLocation.Unavailable;
        }

        return new RetainerDisplayLocation(
            true,
            positions);
    }

    private static int FindDisplayIndex(
        IReadOnlyList<(int slotIndex, int containerIndex)> coordinates,
        int physicalContainerIndex,
        int physicalSlot)
    {
        for (var index = 0;
             index < coordinates.Count;
             index++)
        {
            var coordinate =
                coordinates[index];

            if (coordinate.containerIndex ==
                    physicalContainerIndex &&
                coordinate.slotIndex ==
                    physicalSlot)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsSamePhysicalSource(
        InventorySource left,
        InventorySource right) =>
        left.Storage == right.Storage &&
        left.OwnerId == right.OwnerId &&
        left.Container == right.Container &&
        left.ParentCharacterId == right.ParentCharacterId;

}
