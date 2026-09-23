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
/// Presentation-only mapping from the four physical character inventory
/// containers to the four 35-slot pages shown by the game. Uses the ODR sort
/// order so planner/verifier coordinates remain independent from UI layout.
/// </summary>
public sealed class CharacterDisplayLocator
{
    private const int VisibleSlotsPerPage = 35;

    private readonly IOdrScanner odrScanner;
    private readonly ExecutionOrderCompiler executionOrderCompiler;

    public CharacterDisplayLocator(
        IOdrScanner odrScanner,
        ExecutionOrderCompiler executionOrderCompiler)
    {
        this.odrScanner = odrScanner;
        this.executionOrderCompiler = executionOrderCompiler;
    }

    public CharacterDisplayLocation LocateSource(
        PlannerPlan plan,
        int actionIndex,
        IReadOnlyList<InventoryItemSnapshot>? currentItems = null,
        PlannerAction? executionAction = null)
    {
        if (actionIndex < 0 ||
            actionIndex >= plan.Actions.Count)
        {
            return CharacterDisplayLocation.Unavailable;
        }

        var action =
            executionAction ??
            plan.Actions[actionIndex];

        if (action.Type != PlannerActionType.Move ||
            action.Source is null ||
            action.Source.Storage != StorageType.CharacterInventory)
        {
            return CharacterDisplayLocation.Unavailable;
        }

        var source =
            action.Source;

        if (GetPhysicalContainerIndex(source.Container) < 0)
            return CharacterDisplayLocation.Unavailable;

        var sortOrder =
            odrScanner.GetSortOrder(
                source.OwnerId);

        if (sortOrder is null ||
            !sortOrder.NormalInventories.TryGetValue(
                "PlayerInventory",
                out var coordinates) ||
            coordinates.Count == 0)
        {
            return CharacterDisplayLocation.Unavailable;
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
            executionOrderCompiler.OrderStacksForExecution(
                source,
                (currentItems ?? plan.InitialState.Items)
                .Where(item =>
                    item.Storage == source.Storage &&
                    item.OwnerId == source.OwnerId &&
                    (currentItems is not null ||
                     item.Container == source.Container) &&
                    GetPhysicalContainerIndex(item.Container) >= 0 &&
                    item.BaseItemId == action.BaseItemId &&
                    item.IsHq == action.IsHq &&
                    item.Quantity > 0));

        var remaining =
            action.Quantity;

        var quantityToSkip =
            previouslyPlannedFromSameSource;

        var positions =
            new List<CharacterDisplayPosition>();

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

            var physicalContainerIndex =
                GetPhysicalContainerIndex(
                    stack.Container);

            if (physicalContainerIndex < 0)
                return CharacterDisplayLocation.Unavailable;

            var displayIndex =
                FindDisplayIndex(
                    coordinates,
                    physicalContainerIndex,
                    stack.Slot);

            if (displayIndex < 0)
            {
                return CharacterDisplayLocation.Unavailable;
            }

            positions.Add(
                new CharacterDisplayPosition(
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

    private static bool IsSamePhysicalSource(
        InventorySource left,
        InventorySource right) =>
        left.Storage == right.Storage &&
        left.OwnerId == right.OwnerId &&
        left.Container == right.Container &&
        left.ParentCharacterId == right.ParentCharacterId;
}
