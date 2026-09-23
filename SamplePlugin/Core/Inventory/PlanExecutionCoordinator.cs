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
    PlannerDecision? VerifiedDecision,
    string? ReplanReason);

/// <summary>
/// Owns execution progress for logical planner decisions.
///
/// Physical stack guidance may be rematerialized while the logical quantities
/// are unchanged. Once a real MOVE delta appears, cumulative source and
/// destination deltas from the original baseline are authoritative.
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
        session?.Reset();
        ClearCurrentDecisionState();
    }

    public void Clear()
    {
        session = null;
        ClearCurrentDecisionState();
    }

    public bool TryMarkCurrentDecisionExecuted(
        out PlannerDecision? executedDecision)
    {
        if (session is null)
        {
            executedDecision = null;
            return false;
        }

        return session.TryMarkCurrentDecisionExecuted(
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

        if (decision.Type ==
            PlannerDecisionType.SwitchCharacter)
        {
            return UpdateSwitch(
                decision,
                verification);
        }

        var observation =
            verifier.Observe(
                decision,
                inventoryIndex);

        if (!HasFreshMoveObservation(
                baseline,
                observation))
        {
            RefreshGuidanceIfLogicalStateUnchanged(
                decision,
                observation,
                inventoryIndex);

            return Snapshot(
                PlanExecutionCoordinatorStatus.WaitingForObservation);
        }

        reconciliation =
            reconciler.Reconcile(
                decision,
                baseline,
                observation);

        if (verification.Status ==
            PlanExecutionVerificationStatus.Mismatch)
        {
            if (!IsMismatchObservationSettled(
                    observation))
            {
                return Snapshot(
                    PlanExecutionCoordinatorStatus.WaitingForObservation);
            }

            replanReason =
                verification.Message;

            return Snapshot(
                PlanExecutionCoordinatorStatus.ReplanRequired);
        }

        ResetPendingMismatch();

        if (reconciliation.ReconciledQuantity <= 0)
        {
            reconciliation = null;

            RefreshGuidanceIfLogicalStateUnchanged(
                decision,
                observation,
                inventoryIndex);

            return Snapshot(
                PlanExecutionCoordinatorStatus.Pending);
        }

        if (verification.Status ==
            PlanExecutionVerificationStatus.Verified)
        {
            if (!session.IsCurrentDecisionExecuted)
            {
                session.TryMarkCurrentDecisionExecuted(
                    out _);
            }

            session.TryMarkCurrentDecisionVerified();

            var verifiedDecision =
                decision;

            ClearCurrentDecisionState();

            return Snapshot(
                session.IsComplete
                    ? GetCompletedStatus()
                    : PlanExecutionCoordinatorStatus.Verified,
                verifiedDecision);
        }

        if (!session.IsCurrentDecisionExecuted)
        {
            session.TryMarkCurrentDecisionExecuted(
                out _);
        }

        var remainingQuantity =
            reconciliation.RemainingQuantity;

        if (remainingQuantity <= 0)
        {
            replanReason =
                "Il MOVE non risulta verificato ma non rimane quantità da materializzare.";

            return Snapshot(
                PlanExecutionCoordinatorStatus.ReplanRequired);
        }

        var materialization =
            materializer.Materialize(
                decision,
                inventoryIndex.Items,
                remainingQuantity);

        if (!materialization.IsAvailable ||
            materialization.Instruction is null)
        {
            currentInstruction = null;
            replanReason =
                materialization.Message;

            return Snapshot(
                PlanExecutionCoordinatorStatus.ReplanRequired);
        }

        currentInstruction =
            materialization.Instruction;
        replanReason = null;

        return Snapshot(
            PlanExecutionCoordinatorStatus.Executed);
    }

    private PlanExecutionCoordinatorSnapshot UpdateSwitch(
        PlannerDecision decision,
        PlanExecutionVerificationResult switchVerification)
    {
        if (session is null)
        {
            return Snapshot(
                PlanExecutionCoordinatorStatus.Idle);
        }

        if (switchVerification.Status !=
            PlanExecutionVerificationStatus.Verified)
        {
            return Snapshot(
                PlanExecutionCoordinatorStatus.Pending);
        }

        if (!session.IsCurrentDecisionExecuted)
        {
            session.TryMarkCurrentDecisionExecuted(
                out _);
        }

        session.TryMarkCurrentDecisionVerified();
        ClearCurrentDecisionState();

        return Snapshot(
            session.IsComplete
                ? GetCompletedStatus()
                : PlanExecutionCoordinatorStatus.Verified,
            decision);
    }

    private bool EnsureCurrentDecision(
        PlannerDecision decision,
        InventoryIndex inventoryIndex)
    {
        if (session is null)
            return false;

        if (baseline is not null &&
            baseline.DecisionIndex ==
                session.CurrentDecisionIndex)
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
                session.CurrentDecisionIndex,
                decision,
                inventoryIndex);

        verification = null;
        reconciliation = null;
        replanReason = null;
        ResetPendingMismatch();

        return true;
    }

    private void RefreshGuidanceIfLogicalStateUnchanged(
        PlannerDecision decision,
        PlanExecutionObservation observation,
        InventoryIndex inventoryIndex)
    {
        if (baseline is null ||
            observation.SourceQuantity !=
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

            replanReason = null;
        }
    }

    private static bool HasFreshMoveObservation(
        PlanExecutionBaseline baseline,
        PlanExecutionObservation observation) =>
        IsNewerObservation(
            observation.SourceObservationRevision,
            baseline.SourceObservationRevision) &&
        IsNewerObservation(
            observation.DestinationObservationRevision,
            baseline.DestinationObservationRevision);

    private static bool IsNewerObservation(
        long? current,
        long? baseline)
    {
        if (!current.HasValue)
            return false;

        return !baseline.HasValue ||
               current.Value >
               baseline.Value;
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
        PlanExecutionCoordinatorStatus status,
        PlannerDecision? verifiedDecision = null) =>
        new(
            status,
            session,
            currentInstruction,
            verification,
            reconciliation,
            verifiedDecision,
            replanReason);
}
