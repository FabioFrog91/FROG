using System;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

public enum PlanExecutionStepStatus
{
    Pending,
    Executed,
    Verified
}

public sealed record PlanExecutionStep(
    int Index,
    PlannerAction Action,
    PlanExecutionStepStatus Status);

/// <summary>
/// Tracks execution progress against the immutable planner result.
///
/// Planner actions keep their original identity/order for audit, while
/// execution may verify commutable logical MOVE groups non-contiguously.
/// </summary>
public sealed class PlanExecutionSession
{
    private readonly PlannerPlan plan;
    private readonly bool[] verifiedActions;
    private HashSet<int> currentExecutedActionIndices = new();

    public PlannerPlan Plan => plan;

    public int TotalActionCount =>
        plan.Actions.Count;

    public int VerifiedActionCount =>
        verifiedActions.Count(value =>
            value);

    public int ExecutedActionCount =>
        VerifiedActionCount +
        currentExecutedActionIndices.Count;

    public int RemainingActionCount =>
        TotalActionCount -
        VerifiedActionCount;

    public bool IsComplete =>
        VerifiedActionCount >=
        TotalActionCount;

    public bool IsCurrentActionExecuted =>
        currentExecutedActionIndices.Count > 0;

    public PlannerAction? CurrentAction
    {
        get
        {
            var index =
                GetCurrentActionZeroBasedIndex();

            return index < 0
                ? null
                : plan.Actions[index];
        }
    }

    public int CurrentActionIndex
    {
        get
        {
            var index =
                GetCurrentActionZeroBasedIndex();

            return index < 0
                ? TotalActionCount
                : index + 1;
        }
    }

    public IReadOnlyCollection<int> VerifiedActionIndices =>
        Enumerable
            .Range(
                0,
                verifiedActions.Length)
            .Where(index =>
                verifiedActions[index])
            .ToArray();

    public IReadOnlyCollection<int> PendingActionIndices =>
        Enumerable
            .Range(
                0,
                verifiedActions.Length)
            .Where(index =>
                !verifiedActions[index])
            .ToArray();

    public IReadOnlyList<PlanExecutionStep> Steps =>
        plan.Actions
            .Select((action, index) =>
                new PlanExecutionStep(
                    index + 1,
                    action,
                    verifiedActions[index]
                        ? PlanExecutionStepStatus.Verified
                        : currentExecutedActionIndices.Contains(
                            index)
                            ? PlanExecutionStepStatus.Executed
                            : PlanExecutionStepStatus.Pending))
            .ToArray();

    public PlanExecutionSession(
        PlannerPlan plan)
    {
        this.plan =
            plan ?? throw new ArgumentNullException(
                nameof(plan));

        verifiedActions =
            new bool[
                plan.Actions.Count];
    }

    public bool TryMarkCurrentExecuted(
        IReadOnlyCollection<int> actionIndices,
        out PlannerAction? executedAction)
    {
        executedAction =
            CurrentAction;

        if (executedAction is null ||
            currentExecutedActionIndices.Count > 0 ||
            actionIndices.Count == 0)
        {
            return false;
        }

        var currentIndex =
            GetCurrentActionZeroBasedIndex();

        if (currentIndex < 0 ||
            !actionIndices.Contains(
                currentIndex))
        {
            return false;
        }

        foreach (var index in actionIndices)
        {
            if (index < 0 ||
                index >= TotalActionCount ||
                verifiedActions[index])
            {
                return false;
            }
        }

        currentExecutedActionIndices =
            actionIndices.ToHashSet();

        return true;
    }

    public bool TryMarkCurrentExecuted(
        out PlannerAction? executedAction)
    {
        var currentIndex =
            GetCurrentActionZeroBasedIndex();

        return currentIndex >= 0 &&
               TryMarkCurrentExecuted(
                   new[] { currentIndex },
                   out executedAction);
    }

    public bool TryMarkCurrentVerified(
        IReadOnlyCollection<int> actionIndices)
    {
        if (currentExecutedActionIndices.Count == 0 ||
            currentExecutedActionIndices.Count !=
                actionIndices.Count ||
            !currentExecutedActionIndices.SetEquals(
                actionIndices))
        {
            return false;
        }

        foreach (var index in actionIndices)
        {
            if (index < 0 ||
                index >= TotalActionCount ||
                verifiedActions[index])
            {
                return false;
            }
        }

        foreach (var index in actionIndices)
        {
            verifiedActions[index] =
                true;
        }

        currentExecutedActionIndices.Clear();

        return true;
    }

    public bool TryMarkCurrentVerified()
    {
        var currentIndex =
            GetCurrentActionZeroBasedIndex();

        return currentIndex >= 0 &&
               TryMarkCurrentVerified(
                   new[] { currentIndex });
    }

    public void Reset()
    {
        Array.Clear(
            verifiedActions,
            0,
            verifiedActions.Length);

        currentExecutedActionIndices.Clear();
    }

    private int GetCurrentActionZeroBasedIndex()
    {
        for (var index = 0;
             index < verifiedActions.Length;
             index++)
        {
            if (!verifiedActions[index])
                return index;
        }

        return -1;
    }
}
