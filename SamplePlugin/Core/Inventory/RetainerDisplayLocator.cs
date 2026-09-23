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
/// Pure presentation mapping from already materialized physical Retainer
/// stacks to the game's visible ODR page/slot coordinates.
/// </summary>
public sealed class RetainerDisplayLocator
{
    private const uint RetainerContainerFirst = 10000;
    private const uint RetainerContainerLast = 10006;
    private const int VisibleSlotsPerPage = 35;

    private readonly IOdrScanner odrScanner;

    public RetainerDisplayLocator(
        IOdrScanner odrScanner)
    {
        this.odrScanner =
            odrScanner;
    }

    public RetainerDisplayLocation LocateSource(
        ExecutionInstruction instruction)
    {
        var decision =
            instruction.Decision;

        if (decision.Type !=
                PlannerDecisionType.Move ||
            decision.Source is null ||
            decision.Source.Storage !=
                StorageType.Retainer ||
            decision.Source.ParentCharacterId == 0 ||
            instruction.SourceStacks.Count == 0)
        {
            return RetainerDisplayLocation.Unavailable;
        }

        var sortOrder =
            odrScanner.GetSortOrder(
                decision.Source.ParentCharacterId);

        if (sortOrder is null ||
            !sortOrder.RetainerInventories.TryGetValue(
                decision.Source.OwnerId,
                out var retainerSortOrder))
        {
            return RetainerDisplayLocation.Unavailable;
        }

        var coordinates =
            retainerSortOrder.InventoryCoords;

        if (coordinates.Count == 0)
            return RetainerDisplayLocation.Unavailable;

        var positions =
            new List<RetainerDisplayPosition>();

        foreach (var allocation in
                 instruction.SourceStacks)
        {
            var item =
                allocation.Item;

            if (item.Storage != StorageType.Retainer ||
                item.OwnerId !=
                    decision.Source.OwnerId ||
                item.ParentCharacterId !=
                    decision.Source.ParentCharacterId ||
                item.Container <
                    RetainerContainerFirst ||
                item.Container >
                    RetainerContainerLast ||
                allocation.Quantity <= 0)
            {
                return RetainerDisplayLocation.Unavailable;
            }

            var physicalContainerIndex =
                checked(
                    (int)(item.Container -
                          RetainerContainerFirst));

            var displayIndex =
                FindDisplayIndex(
                    coordinates,
                    physicalContainerIndex,
                    item.Slot);

            if (displayIndex < 0)
                return RetainerDisplayLocation.Unavailable;

            positions.Add(
                new RetainerDisplayPosition(
                    displayIndex /
                        VisibleSlotsPerPage +
                        1,
                    displayIndex %
                        VisibleSlotsPerPage +
                        1,
                    allocation.Quantity));
        }

        if (positions.Sum(position =>
                position.Quantity) !=
            instruction.Quantity)
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
}
