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
    PlannerDecision Decision,
    PlanExecutionStepStatus Status);

/// <summary>
/// Tracks execution progress for the immutable logical planner decisions.
///
/// Planned, Executed and Verified remain separate concepts. Physical stack
/// materialization is owned elsewhere and never changes session progress.
/// </summary>
public sealed class PlanExecutionSession
{
    private readonly PlannerPlan plan;
    private int verifiedDecisionCount;
    private bool currentDecisionExecuted;

    public PlannerPlan Plan =>
        plan;

    public int TotalDecisionCount =>
        plan.Decisions.Count;

    public int VerifiedDecisionCount =>
        verifiedDecisionCount;

    public int ExecutedDecisionCount =>
        verifiedDecisionCount +
        (currentDecisionExecuted
            ? 1
            : 0);

    public int RemainingDecisionCount =>
        TotalDecisionCount -
        VerifiedDecisionCount;

    public bool IsComplete =>
        verifiedDecisionCount >=
        TotalDecisionCount;

    public bool IsCurrentDecisionExecuted =>
        currentDecisionExecuted;

    public PlannerDecision? CurrentDecision =>
        IsComplete
            ? null
            : plan.Decisions[
                verifiedDecisionCount];

    public int CurrentDecisionIndex =>
        IsComplete
            ? TotalDecisionCount
            : verifiedDecisionCount + 1;

    public IReadOnlyList<PlanExecutionStep> Steps =>
        plan.Decisions
            .Select((decision, index) =>
                new PlanExecutionStep(
                    index + 1,
                    decision,
                    index < verifiedDecisionCount
                        ? PlanExecutionStepStatus.Verified
                        : index == verifiedDecisionCount &&
                          currentDecisionExecuted
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

    public bool TryMarkCurrentDecisionExecuted(
        out PlannerDecision? executedDecision)
    {
        executedDecision =
            CurrentDecision;

        if (executedDecision is null ||
            currentDecisionExecuted)
        {
            return false;
        }

        currentDecisionExecuted =
            true;

        return true;
    }

    public bool TryMarkCurrentDecisionVerified()
    {
        if (!currentDecisionExecuted ||
            IsComplete)
        {
            return false;
        }

        verifiedDecisionCount++;
        currentDecisionExecuted = false;

        return true;
    }

    public void Reset()
    {
        verifiedDecisionCount = 0;
        currentDecisionExecuted = false;
    }
}
