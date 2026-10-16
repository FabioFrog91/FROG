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
    private readonly List<PlannerDecision> decisions;
    private int verifiedDecisionCount;
    private bool currentDecisionExecuted;

    public PlannerPlan Plan =>
        plan;

    public IReadOnlyList<PlannerDecision> Decisions =>
        decisions;

    public int TotalDecisionCount =>
        decisions.Count;

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
            : decisions[
                verifiedDecisionCount];

    public int CurrentDecisionIndex =>
        IsComplete
            ? TotalDecisionCount
            : verifiedDecisionCount + 1;

    public IReadOnlyList<PlanExecutionStep> Steps =>
        decisions
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

        decisions =
            plan.Decisions.ToList();
    }

    public bool TryApplyPendingOrder(
        int startIndex,
        IReadOnlyList<PlannerDecision> orderedDecisions)
    {
        if (orderedDecisions is null ||
            orderedDecisions.Count < 2 ||
            startIndex < verifiedDecisionCount + 1 ||
            startIndex < 0 ||
            startIndex + orderedDecisions.Count >
                decisions.Count)
        {
            return false;
        }

        var current =
            decisions
                .Skip(startIndex)
                .Take(orderedDecisions.Count)
                .ToList();

        if (!HaveSameDecisions(
                current,
                orderedDecisions) ||
            current.SequenceEqual(
                orderedDecisions))
        {
            return false;
        }

        for (var index = 0;
             index < orderedDecisions.Count;
             index++)
        {
            decisions[startIndex + index] =
                orderedDecisions[index];
        }

        return true;
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

        decisions.Clear();
        decisions.AddRange(
            plan.Decisions);
    }

    private static bool HaveSameDecisions(
        IReadOnlyList<PlannerDecision> current,
        IReadOnlyList<PlannerDecision> candidate)
    {
        if (current.Count != candidate.Count)
            return false;

        var remaining =
            current.ToList();

        foreach (var decision in candidate)
        {
            var index =
                remaining.FindIndex(existing =>
                    Equals(
                        existing,
                        decision));

            if (index < 0)
                return false;

            remaining.RemoveAt(
                index);
        }

        return remaining.Count == 0;
    }
}
