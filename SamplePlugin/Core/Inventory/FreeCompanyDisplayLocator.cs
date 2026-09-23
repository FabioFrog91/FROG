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
/// Pure presentation mapping from already materialized physical FC stacks to
/// visible page/slot coordinates.
/// </summary>
public sealed class FreeCompanyDisplayLocator
{
    private const uint FirstContainer =
        (uint)GameInventoryType.FreeCompanyPage1;

    private const uint LastContainer =
        (uint)GameInventoryType.FreeCompanyPage5;

    public FreeCompanyDisplayLocation LocateSource(
        ExecutionInstruction instruction)
    {
        var decision =
            instruction.Decision;

        if (decision.Type !=
                PlannerDecisionType.Move ||
            decision.Source is null ||
            decision.Source.Storage !=
                StorageType.FreeCompanyChest ||
            instruction.SourceStacks.Count == 0)
        {
            return FreeCompanyDisplayLocation.Unavailable;
        }

        var positions =
            new List<FreeCompanyDisplayPosition>();

        foreach (var allocation in
                 instruction.SourceStacks)
        {
            var item =
                allocation.Item;

            if (item.Storage !=
                    StorageType.FreeCompanyChest ||
                item.OwnerId !=
                    decision.Source.OwnerId ||
                item.Container < FirstContainer ||
                item.Container > LastContainer ||
                allocation.Quantity <= 0)
            {
                return FreeCompanyDisplayLocation.Unavailable;
            }

            positions.Add(
                new FreeCompanyDisplayPosition(
                    checked(
                        (int)(item.Container -
                              FirstContainer) +
                        1),
                    item.Slot + 1,
                    allocation.Quantity));
        }

        if (positions.Sum(position =>
                position.Quantity) !=
            decision.Quantity)
        {
            return FreeCompanyDisplayLocation.Unavailable;
        }

        return new FreeCompanyDisplayLocation(
            true,
            positions);
    }
}
