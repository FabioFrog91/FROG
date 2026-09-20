using System;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

public enum PlanExecutionStepStatus
{
    Pending,
    Executed
}

public sealed record PlanExecutionStep(
    int Index,
    PlannerAction Action,
    PlanExecutionStepStatus Status);

/// <summary>
/// Tracks execution progress for an immutable planner result.
///
/// Planned, Executed and Verified are deliberately separate concepts.
/// This session only records that the caller says an action was executed.
/// A future verifier/reconciler can compare observed inventory state against
/// the plan without changing planner semantics.
/// </summary>
public sealed class PlanExecutionSession
{
    private readonly PlannerPlan plan;
    private int executedActionCount;

    public PlannerPlan Plan => plan;

    public int TotalActionCount =>
        plan.Actions.Count;

    public int ExecutedActionCount =>
        executedActionCount;

    public int RemainingActionCount =>
        TotalActionCount - ExecutedActionCount;

    public bool IsComplete =>
        executedActionCount >= TotalActionCount;

    public PlannerAction? CurrentAction =>
        IsComplete
            ? null
            : plan.Actions[executedActionCount];

    public IReadOnlyList<PlanExecutionStep> Steps =>
        plan.Actions
            .Select((action, index) =>
                new PlanExecutionStep(
                    index + 1,
                    action,
                    index < executedActionCount
                        ? PlanExecutionStepStatus.Executed
                        : PlanExecutionStepStatus.Pending))
            .ToArray();

    public PlanExecutionSession(
        PlannerPlan plan)
    {
        this.plan =
            plan ?? throw new ArgumentNullException(
                nameof(plan));
    }

    public bool TryMarkCurrentExecuted(
        out PlannerAction? executedAction)
    {
        executedAction = CurrentAction;

        if (executedAction is null)
            return false;

        executedActionCount++;

        return true;
    }

    public void Reset()
    {
        executedActionCount = 0;
    }
}
