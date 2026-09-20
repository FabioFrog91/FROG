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

        var state =
            BuildStateBeforeAction(
                plan,
                actionIndex);

        var matchingStacks =
            state.Find(source)
                .Where(item =>
                    item.BaseItemId == action.BaseItemId &&
                    item.IsHq == action.IsHq &&
                    item.Quantity > 0)
                .OrderBy(item =>
                    item.Slot)
                .ToList();

        var remaining =
            action.Quantity;

        var positions =
            new List<RetainerDisplayPosition>();

        var physicalContainerIndex =
            checked(
                (int)(source.Container -
                      RetainerContainerFirst));

        foreach (var stack in matchingStacks)
        {
            if (remaining <= 0)
                break;

            var moved =
                Math.Min(
                    remaining,
                    stack.Quantity);

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

    private static PlannerState BuildStateBeforeAction(
        PlannerPlan plan,
        int actionIndex)
    {
        var state =
            plan.InitialState;

        for (var index = 0;
             index < actionIndex;
             index++)
        {
            var action =
                plan.Actions[index];

            state =
                action.Type switch
                {
                    PlannerActionType.Move
                        when action.Source is not null &&
                             action.Destination is not null =>
                        state.Move(
                            action.Source,
                            action.Destination,
                            action.BaseItemId,
                            action.IsHq,
                            action.Quantity),

                    PlannerActionType.SwitchCharacter =>
                        state.WithCurrentCharacter(
                            action.ToCharacterId),

                    _ =>
                        state
                };
        }

        return state;
    }
}
