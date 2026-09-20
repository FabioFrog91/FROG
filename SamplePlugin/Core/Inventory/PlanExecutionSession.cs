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
/// Tracks execution progress for an immutable planner result.
///
/// Planned, Executed and Verified are deliberately separate concepts.
/// An action is not advanced until it has been verified.
/// </summary>
public sealed class PlanExecutionSession
{
    private readonly PlannerPlan plan;
    private int verifiedActionCount;
    private bool currentActionExecuted;

    public PlannerPlan Plan => plan;

    public int TotalActionCount =>
        plan.Actions.Count;

    public int VerifiedActionCount =>
        verifiedActionCount;

    public int ExecutedActionCount =>
        verifiedActionCount +
        (currentActionExecuted ? 1 : 0);

    public int RemainingActionCount =>
        TotalActionCount - VerifiedActionCount;

    public bool IsComplete =>
        verifiedActionCount >= TotalActionCount;

    public bool IsCurrentActionExecuted =>
        currentActionExecuted;

    public PlannerAction? CurrentAction =>
        IsComplete
            ? null
            : plan.Actions[verifiedActionCount];

    public int CurrentActionIndex =>
        IsComplete
            ? TotalActionCount
            : verifiedActionCount + 1;

    public IReadOnlyList<PlanExecutionStep> Steps =>
        plan.Actions
            .Select((action, index) =>
                new PlanExecutionStep(
                    index + 1,
                    action,
                    index < verifiedActionCount
                        ? PlanExecutionStepStatus.Verified
                        : index == verifiedActionCount &&
                          currentActionExecuted
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

        if (executedAction is null ||
            currentActionExecuted)
        {
            return false;
        }

        currentActionExecuted = true;
        return true;
    }

    public bool TryMarkCurrentVerified()
    {
        if (!currentActionExecuted ||
            IsComplete)
        {
            return false;
        }

        verifiedActionCount++;
        currentActionExecuted = false;

        return true;
    }

    public void Reset()
    {
        verifiedActionCount = 0;
        currentActionExecuted = false;
    }
}
