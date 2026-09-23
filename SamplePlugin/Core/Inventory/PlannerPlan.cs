using System;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

public enum PlannerPlanResult
{
    Completed,
    CompletedWithMissing
}

public sealed record PlannerCapacityBlock(
    InventorySource Source,
    InventorySource Destination,
    uint BaseItemId,
    bool IsHq,
    int Quantity,
    int MaximumStack)
{
    public int MinimumAdditionalSlots =>
        Quantity <= 0 || MaximumStack <= 0
            ? 0
            : checked((int)Math.Min(
                int.MaxValue,
                ((long)Quantity + MaximumStack - 1) /
                MaximumStack));
}

public sealed record PlannerCapacityAdvice(
    InventorySource Destination,
    int MinimumAdditionalSlots,
    int BlockedQuantity);

public sealed class PlannerPlan
{
    private readonly List<PlannerAction> actions;
    private readonly IReadOnlyList<PlannerDecision> decisions;
    private readonly List<PlannerCapacityBlock> capacityBlocks;
    private readonly List<PlannerCapacityAdvice> capacityAdvice;

    public PlannerState InitialState { get; }
    public PlannerState FinalState { get; }
    public IReadOnlyList<PlannerAction> Actions => actions;
    public IReadOnlyList<PlannerDecision> Decisions => decisions;
    public PlannerPlanResult Result { get; }
    public int Missing { get; }
    public int CapacityBlocked { get; }
    public IReadOnlyList<PlannerCapacityBlock> CapacityBlocks =>
        capacityBlocks;
    public IReadOnlyList<PlannerCapacityAdvice> CapacityAdvice =>
        capacityAdvice;
    public PlannerCapacityBlock? NextCapacityBlock =>
        capacityBlocks.FirstOrDefault();

    public int CharacterSwitches =>
        actions.Count(action =>
            action.Type == PlannerActionType.SwitchCharacter);

    public int RetainerAccesses =>
        actions
            .Where(action =>
                action.Type == PlannerActionType.Move &&
                action.Source?.Storage == StorageType.Retainer)
            .Select(action =>
                $"{action.Source!.OwnerId}:{action.Source.ParentCharacterId}")
            .Distinct()
            .Count();

    public int TransferHops =>
        actions
            .Where(action =>
                action.Type == PlannerActionType.Move &&
                action.Source is not null &&
                action.Destination is not null)
            .Select(action =>
                $"{action.Source!.Storage}:{action.Source.OwnerId}:{action.Source.Container}" +
                $">" +
                $"{action.Destination!.Storage}:{action.Destination.OwnerId}:{action.Destination.Container}")
            .Distinct()
            .Count();

    public PlannerPlan(
        PlannerState initialState,
        PlannerState finalState,
        IEnumerable<PlannerAction> actions,
        PlannerPlanResult result,
        int missing,
        int capacityBlocked = 0,
        IEnumerable<PlannerCapacityBlock>? capacityBlocks = null)
        : this(
            initialState,
            finalState,
            actions.ToList(),
            result,
            missing,
            capacityBlocked,
            capacityBlocks?.ToList() ??
            new List<PlannerCapacityBlock>(),
            decisions: null)
    {
    }

    private PlannerPlan(
        PlannerState initialState,
        PlannerState finalState,
        List<PlannerAction> actions,
        PlannerPlanResult result,
        int missing,
        int capacityBlocked,
        List<PlannerCapacityBlock> capacityBlocks,
        IReadOnlyList<PlannerDecision>? decisions)
    {
        InitialState = initialState;
        FinalState = finalState;
        this.actions = actions;
        this.decisions =
            decisions ??
            PlannerDecisionCompiler.Compile(
                actions);
        Result = result;
        Missing = missing;
        CapacityBlocked = Math.Clamp(
            capacityBlocked,
            0,
            missing);
        this.capacityBlocks =
            capacityBlocks.ToList();
        capacityAdvice =
            BuildCapacityAdvice(
                this.capacityBlocks);
    }

    public PlannerPlan Append(
        PlannerAction action,
        PlannerState state)
    {
        var updatedActions =
            new List<PlannerAction>(
                actions.Count + 1);

        updatedActions.AddRange(actions);
        updatedActions.Add(action);

        return new PlannerPlan(
            InitialState,
            state,
            updatedActions,
            Result,
            Missing,
            CapacityBlocked,
            capacityBlocks,
            decisions);
    }

    public PlannerPlan WithResult(
        PlannerPlanResult result,
        int missing) =>
        new(
            InitialState,
            FinalState,
            actions,
            result,
            missing,
            Math.Min(
                CapacityBlocked,
                missing),
            capacityBlocks);

    public PlannerPlan WithActions(
        IEnumerable<PlannerAction> reorderedActions) =>
        new(
            InitialState,
            FinalState,
            reorderedActions,
            Result,
            Missing,
            CapacityBlocked,
            capacityBlocks);

    private static List<PlannerCapacityAdvice> BuildCapacityAdvice(
        IReadOnlyList<PlannerCapacityBlock> blocks) =>
        blocks
            .GroupBy(block =>
                new
                {
                    block.Destination.Storage,
                    block.Destination.OwnerId
                })
            .Select(destinationGroup =>
            {
                var minimumAdditionalSlots =
                    destinationGroup
                        .GroupBy(block =>
                            new
                            {
                                block.BaseItemId,
                                block.IsHq,
                                block.MaximumStack
                            })
                        .Sum(stackGroup =>
                        {
                            var quantity =
                                stackGroup.Sum(block =>
                                    (long)block.Quantity);

                            return (quantity + stackGroup.Key.MaximumStack - 1) /
                                   stackGroup.Key.MaximumStack;
                        });

                var blockedQuantity =
                    destinationGroup.Sum(block =>
                        (long)block.Quantity);

                return new PlannerCapacityAdvice(
                    destinationGroup.First().Destination,
                    checked((int)Math.Min(
                        int.MaxValue,
                        minimumAdditionalSlots)),
                    checked((int)Math.Min(
                        int.MaxValue,
                        blockedQuantity)));
            })
            .ToList();
}
