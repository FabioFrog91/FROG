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
        int missing)
        : this(
            initialState,
            finalState,
            actions.ToList(),
            result,
            missing)
    {
    }

    private PlannerPlan(
        PlannerState initialState,
        PlannerState finalState,
        List<PlannerAction> actions,
        PlannerPlanResult result,
        int missing)
    {
        InitialState = initialState;
        FinalState = finalState;
        this.actions = actions;
        Result = result;
        Missing = missing;
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
            Missing);
    }

    public PlannerPlan WithResult(
        PlannerPlanResult result,
        int missing) =>
        new(
            InitialState,
            FinalState,
            actions,
            result,
            missing);
}
