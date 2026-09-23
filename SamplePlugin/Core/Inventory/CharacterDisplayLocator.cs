using CriticalCommonLib.Services;
using Dalamud.Game.Inventory;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

public sealed record CharacterDisplayPosition(
    int Page,
    int Slot,
    int Quantity);

public sealed record CharacterDisplayLocation(
    bool IsAvailable,
    IReadOnlyList<CharacterDisplayPosition> Positions)
{
    public static CharacterDisplayLocation Unavailable { get; } =
        new(
            false,
            Array.Empty<CharacterDisplayPosition>());
}

/// <summary>
/// Pure presentation mapping from already materialized physical character
/// inventory stacks to visible ODR page/slot coordinates.
/// </summary>
public sealed class CharacterDisplayLocator
{
    private const int VisibleSlotsPerPage = 35;

    private readonly IOdrScanner odrScanner;

    public CharacterDisplayLocator(
        IOdrScanner odrScanner)
    {
        this.odrScanner =
            odrScanner;
    }

    public CharacterDisplayLocation LocateSource(
        ExecutionInstruction instruction)
    {
        var decision =
            instruction.Decision;

        if (decision.Type !=
                PlannerDecisionType.Move ||
            decision.Source is null ||
            decision.Source.Storage !=
                StorageType.CharacterInventory ||
            instruction.SourceStacks.Count == 0)
        {
            return CharacterDisplayLocation.Unavailable;
        }

        var sortOrder =
            odrScanner.GetSortOrder(
                decision.Source.OwnerId);

        if (sortOrder is null ||
            !sortOrder.NormalInventories.TryGetValue(
                "PlayerInventory",
                out var coordinates) ||
            coordinates.Count == 0)
        {
            return CharacterDisplayLocation.Unavailable;
        }

        var positions =
            new List<CharacterDisplayPosition>();

        foreach (var allocation in
                 instruction.SourceStacks)
        {
            var item =
                allocation.Item;

            if (item.Storage !=
                    StorageType.CharacterInventory ||
                item.OwnerId !=
                    decision.Source.OwnerId ||
                allocation.Quantity <= 0)
            {
                return CharacterDisplayLocation.Unavailable;
            }

            var physicalContainerIndex =
                GetPhysicalContainerIndex(
                    item.Container);

            if (physicalContainerIndex < 0)
                return CharacterDisplayLocation.Unavailable;

            var displayIndex =
                FindDisplayIndex(
                    coordinates,
                    physicalContainerIndex,
                    item.Slot);

            if (displayIndex < 0)
                return CharacterDisplayLocation.Unavailable;

            positions.Add(
                new CharacterDisplayPosition(
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
            return CharacterDisplayLocation.Unavailable;
        }

        return new CharacterDisplayLocation(
            true,
            positions);
    }

    private static int GetPhysicalContainerIndex(
        uint container) =>
        container switch
        {
            (uint)GameInventoryType.Inventory1 => 0,
            (uint)GameInventoryType.Inventory2 => 1,
            (uint)GameInventoryType.Inventory3 => 2,
            (uint)GameInventoryType.Inventory4 => 3,
            _ => -1
        };

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
