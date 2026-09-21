using System;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

public enum PlannerPlanResult
{
    Completed,
    CompletedWithMissing
}

public sealed class PlannerPlan
{
    private readonly List<PlannerAction> actions;

    public PlannerState InitialState { get; }
    public PlannerState FinalState { get; }
    public IReadOnlyList<PlannerAction> Actions => actions;
    public PlannerPlanResult Result { get; }
    public int Missing { get; }
    public int CapacityBlocked { get; }

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
        int capacityBlocked = 0)
        : this(
            initialState,
            finalState,
            actions.ToList(),
            result,
            missing,
            capacityBlocked)
    {
    }

    private PlannerPlan(
        PlannerState initialState,
        PlannerState finalState,
        List<PlannerAction> actions,
        PlannerPlanResult result,
        int missing,
        int capacityBlocked)
    {
        InitialState = initialState;
        FinalState = finalState;
        this.actions = actions;
        Result = result;
        Missing = missing;
        CapacityBlocked = Math.Clamp(
            capacityBlocked,
            0,
            missing);
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
            CapacityBlocked);
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
                missing));

    public PlannerPlan WithActions(
        IEnumerable<PlannerAction> reorderedActions) =>
        new(
            InitialState,
            FinalState,
            reorderedActions,
            Result,
            Missing,
            CapacityBlocked);
}
