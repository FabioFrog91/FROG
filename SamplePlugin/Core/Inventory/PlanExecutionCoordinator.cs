using System;

namespace FROG.Core.Inventory;

public enum PlanExecutionCoordinatorStatus
{
    Idle,
    Pending,
    Executed,
    WaitingForObservation,
    Verified,
    ReplanRequired,
    WaitingForCapacity,
    Complete
}

public readonly record struct PlanExecutionCoordinatorSnapshot(
    PlanExecutionCoordinatorStatus Status,
    PlanExecutionSession? Session,
    ExecutionInstruction? CurrentInstruction,
    PlanExecutionVerificationResult? Verification,
    PlanExecutionReconciliationResult? Reconciliation,
    string? ReplanReason);

/// <summary>
/// Owns execution state for logical planner decisions. Physical stack
/// materialization is refreshed only while the logical MOVE has not started;
/// verification/reconciliation own the state once a real delta is observed.
/// </summary>
public sealed class PlanExecutionCoordinator
{
    private const int MismatchSettleIntervalMs = 250;

    private readonly PlanExecutionVerifier verifier = new();
    private readonly PlanExecutionReconciler reconciler = new();
    private readonly PlanExecutionMaterializer materializer;

    private PlanExecutionSession? session;
    private PlanExecutionBaseline? baseline;
    private ExecutionInstruction? currentInstruction;
    private PlanExecutionVerificationResult? verification;
    private PlanExecutionReconciliationResult? reconciliation;
    private PlanExecutionObservation? pendingMismatchObservation;
    private long pendingMismatchSinceMs;
    private string? replanReason;

    public PlanExecutionCoordinator(
        ExecutionOrderCompiler executionOrderCompiler)
    {
        materializer =
            new PlanExecutionMaterializer(
                executionOrderCompiler);
    }

    public PlanExecutionSession? Session =>
        session;

    public void Start(
        PlannerPlan plan)
    {
        session =
            new PlanExecutionSession(
                plan);

        ClearCurrentDecisionState();
    }

    public void Reset()
    {
        if (session is not null)
            session.Reset();

        ClearCurrentDecisionState();
    }

    public void Clear()
    {
        session = null;
        ClearCurrentDecisionState();
    }

    public bool TryMarkCurrentExecuted(
        out PlannerDecision? executedDecision)
    {
        if (session is null)
        {
            executedDecision = null;
            return false;
        }

        return session.TryMarkCurrentExecuted(
            out executedDecision);
    }

    public PlanExecutionCoordinatorSnapshot Update(
        InventoryIndex inventoryIndex,
        ulong currentCharacterId)
    {
        if (session is null)
        {
            return Snapshot(
                PlanExecutionCoordinatorStatus.Idle);
        }

        if (session.IsComplete)
        {
            return Snapshot(
                GetCompletedStatus());
        }

        var decision =
            session.CurrentDecision;

        if (decision is null)
        {
            return Snapshot(
                GetCompletedStatus());
        }

        if (!EnsureCurrentDecision(
                decision,
                inventoryIndex))
        {
            return Snapshot(
                PlanExecutionCoordinatorStatus.ReplanRequired);
        }

        if (baseline is null)
        {
            return Snapshot(
                PlanExecutionCoordinatorStatus.Pending);
        }

        verification =
            verifier.Verify(
                decision,
                baseline,
                inventoryIndex,
                currentCharacterId);

        if (!session.IsCurrentActionExecuted)
        {
            if (decision.Type ==
                PlannerDecisionType.SwitchCharacter)
            {
                if (verification.Status !=
                    PlanExecutionVerificationStatus.Verified)
                {
                    return Snapshot(
                        PlanExecutionCoordinatorStatus.Pending);
                }

                session.TryMarkCurrentExecuted(
                    out _);
            }
            else
            {
                if (verification.Status ==
                    PlanExecutionVerificationStatus.WaitingForObservation)
                {
                    RefreshGuidanceIfStillUnchanged(
                        decision,
                        inventoryIndex);

                    return Snapshot(
                        PlanExecutionCoordinatorStatus.Pending);
                }

                var observation =
                    verifier.Observe(
                        decision,
                        inventoryIndex);

                if (verification.Status ==
                        PlanExecutionVerificationStatus.Mismatch &&
                    !IsMismatchObservationSettled(
                        observation))
                {
                    return Snapshot(
                        PlanExecutionCoordinatorStatus.WaitingForObservation);
                }

                reconciliation =
                    reconciler.Reconcile(
                        decision,
                        baseline,
                        observation);

                if (reconciliation.SourceDecrease <= 0 &&
                    reconciliation.DestinationIncrease <= 0)
                {
                    reconciliation = null;

                    RefreshGuidanceIfStillUnchanged(
                        decision,
                        inventoryIndex);

                    return Snapshot(
                        PlanExecutionCoordinatorStatus.Pending);
                }

                if (reconciliation.ObservedTransferredQuantity <= 0)
                {
                    return Snapshot(
                        PlanExecutionCoordinatorStatus.WaitingForObservation);
                }

                session.TryMarkCurrentExecuted(
                    out _);

                if (reconciliation.HasVariance)
                {
                    return Snapshot(
                        PlanExecutionCoordinatorStatus.ReplanRequired);
                }
            }
        }

        if (decision.Type ==
                PlannerDecisionType.Move &&
            reconciliation is null &&
            verification.Status !=
                PlanExecutionVerificationStatus.WaitingForObservation)
        {
            var observation =
                verifier.Observe(
                    decision,
                    inventoryIndex);

            reconciliation =
                reconciler.Reconcile(
                    decision,
                    baseline,
                    observation);

            if (reconciliation.HasVariance)
            {
                return Snapshot(
                    PlanExecutionCoordinatorStatus.ReplanRequired);
            }
        }

        if (verification.Status ==
            PlanExecutionVerificationStatus.Verified)
        {
            session.TryMarkCurrentVerified();

            ClearCurrentDecisionState();

            return Snapshot(
                session.IsComplete
                    ? GetCompletedStatus()
                    : PlanExecutionCoordinatorStatus.Verified);
        }

        return Snapshot(
            verification.Status ==
                PlanExecutionVerificationStatus.WaitingForObservation
                ? PlanExecutionCoordinatorStatus.WaitingForObservation
                : PlanExecutionCoordinatorStatus.Executed);
    }

    private bool EnsureCurrentDecision(
        PlannerDecision decision,
        InventoryIndex inventoryIndex)
    {
        if (session is null)
            return false;

        if (baseline is not null &&
            baseline.ActionIndex ==
                session.CurrentActionIndex)
        {
            return true;
        }

        var materialization =
            materializer.Materialize(
                decision,
                inventoryIndex.Items);

        if (!materialization.IsAvailable ||
            materialization.Instruction is null)
        {
            currentInstruction = null;
            replanReason =
                materialization.Message;

            return false;
        }

        currentInstruction =
            materialization.Instruction;

        baseline =
            verifier.CaptureBaseline(
                session.CurrentActionIndex,
                decision,
                inventoryIndex);

        verification = null;
        reconciliation = null;
        replanReason = null;
        ResetPendingMismatch();

        return true;
    }

    private void RefreshGuidanceIfStillUnchanged(
        PlannerDecision decision,
        InventoryIndex inventoryIndex)
    {
        if (baseline is null)
            return;

        var observation =
            verifier.Observe(
                decision,
                inventoryIndex);

        if (observation.SourceQuantity !=
                baseline.SourceQuantity ||
            observation.DestinationQuantity !=
                baseline.DestinationQuantity)
        {
            return;
        }

        var materialization =
            materializer.Materialize(
                decision,
                inventoryIndex.Items);

        if (materialization.IsAvailable &&
            materialization.Instruction is not null)
        {
            currentInstruction =
                materialization.Instruction;
        }
    }

    private PlanExecutionCoordinatorStatus GetCompletedStatus() =>
        session?.Plan.CapacityBlocked > 0
            ? PlanExecutionCoordinatorStatus.WaitingForCapacity
            : PlanExecutionCoordinatorStatus.Complete;

    private bool IsMismatchObservationSettled(
        PlanExecutionObservation observation)
    {
        var nowMs =
            Environment.TickCount64;

        if (pendingMismatchObservation is null ||
            pendingMismatchObservation.SourceQuantity !=
                observation.SourceQuantity ||
            pendingMismatchObservation.DestinationQuantity !=
                observation.DestinationQuantity ||
            pendingMismatchObservation.SourceLayoutFingerprint !=
                observation.SourceLayoutFingerprint)
        {
            pendingMismatchObservation =
                observation;

            pendingMismatchSinceMs =
                nowMs;

            return false;
        }

        return nowMs -
               pendingMismatchSinceMs >=
               MismatchSettleIntervalMs;
    }

    private void ResetPendingMismatch()
    {
        pendingMismatchObservation = null;
        pendingMismatchSinceMs = 0;
    }

    private void ClearCurrentDecisionState()
    {
        baseline = null;
        currentInstruction = null;
        verification = null;
        reconciliation = null;
        replanReason = null;
        ResetPendingMismatch();
    }

    private PlanExecutionCoordinatorSnapshot Snapshot(
        PlanExecutionCoordinatorStatus status) =>
        new(
            status,
            session,
            currentInstruction,
            verification,
            reconciliation,
            replanReason);
}
