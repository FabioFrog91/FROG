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
    PlannerAction? CurrentAction,
    PlanExecutionVerificationResult? Verification,
    PlanExecutionReconciliationResult? Reconciliation);

/// <summary>
/// Owns the execution state machine for a single immutable planner result.
/// UI and future executors can drive the same flow without embedding
/// verification/reconciliation rules in presentation code.
/// </summary>
public sealed class PlanExecutionCoordinator
{
    private const int MismatchSettleIntervalMs = 250;

    private readonly PlanExecutionVerifier verifier = new();
    private readonly PlanExecutionReconciler reconciler = new();
    private readonly PlanExecutionMaterializer materializer = new();

    private PlanExecutionSession? session;
    private PlanExecutionBaseline? baseline;
    private PlanExecutionVerificationResult? verification;
    private PlanExecutionReconciliationResult? reconciliation;
    private PlanExecutionObservation? pendingMismatchObservation;
    private long pendingMismatchSinceMs;

    public PlanExecutionSession? Session =>
        session;

    public void Start(
        PlannerPlan plan)
    {
        session =
            new PlanExecutionSession(
                plan);

        baseline = null;
        verification = null;
        reconciliation = null;
        ResetPendingMismatch();
    }

    public void Reset()
    {
        if (session is not null)
            session.Reset();

        baseline = null;
        verification = null;
        reconciliation = null;
        ResetPendingMismatch();
    }

    public void Clear()
    {
        session = null;
        baseline = null;
        verification = null;
        reconciliation = null;
        ResetPendingMismatch();
    }

    public bool TryMarkCurrentExecuted(
        out PlannerAction? executedAction)
    {
        if (session is null ||
            session.IsComplete)
        {
            executedAction = null;
            return false;
        }

        var materialized =
            materializer.Materialize(
                session.Plan,
                session.CurrentActionIndex - 1,
                session.VerifiedActionIndices);

        executedAction =
            materialized.Action;

        return session.TryMarkCurrentExecuted(
            materialized.CoveredActionIndices,
            out _);
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

        var currentActionIndex =
            session.CurrentActionIndex - 1;

        var materialized =
            materializer.Materialize(
                session.Plan,
                currentActionIndex,
                session.VerifiedActionIndices);

        var action =
            materialized.Action;

        var coveredActionIndices =
            materialized.CoveredActionIndices;

        if (action is null)
        {
            return Snapshot(
                GetCompletedStatus());
        }

        EnsureBaseline(
            action,
            inventoryIndex);

        if (baseline is null)
        {
            return Snapshot(
                PlanExecutionCoordinatorStatus.Pending);
        }

        verification =
            verifier.Verify(
                action,
                baseline,
                inventoryIndex,
                currentCharacterId);

        if (!session.IsCurrentActionExecuted)
        {
            if (action.Type == PlannerActionType.SwitchCharacter)
            {
                if (verification.Status !=
                    PlanExecutionVerificationStatus.Verified)
                {
                    return Snapshot(
                        PlanExecutionCoordinatorStatus.Pending);
                }

                session.TryMarkCurrentExecuted(
                    coveredActionIndices,
                    out _);
            }
            else
            {
                if (verification.Status ==
                    PlanExecutionVerificationStatus.WaitingForObservation)
                {
                    return Snapshot(
                        PlanExecutionCoordinatorStatus.Pending);
                }

                var observation =
                    verifier.Observe(
                        action,
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
                        action,
                        baseline,
                        observation);

                if (reconciliation.SourceDecrease <= 0 &&
                    reconciliation.DestinationIncrease <= 0)
                {
                    reconciliation = null;

                    return Snapshot(
                        PlanExecutionCoordinatorStatus.Pending);
                }

                // A source-only decrease can be a transient inventory
                // observation (notably while FC pages are being scanned).
                // Never advance or replan until at least one unit is
                // confirmed on both sides of the requested transfer.
                if (reconciliation.ObservedTransferredQuantity <= 0)
                {
                    return Snapshot(
                        PlanExecutionCoordinatorStatus.WaitingForObservation);
                }

                session.TryMarkCurrentExecuted(
                    coveredActionIndices,
                    out _);

                if (reconciliation.HasVariance)
                {
                    return Snapshot(
                        PlanExecutionCoordinatorStatus.ReplanRequired);
                }
            }
        }

        if (action.Type == PlannerActionType.Move &&
            reconciliation is null &&
            verification.Status !=
                PlanExecutionVerificationStatus.WaitingForObservation)
        {
            var observation =
                verifier.Observe(
                    action,
                    inventoryIndex);

            reconciliation =
                reconciler.Reconcile(
                    action,
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
            session.TryMarkCurrentVerified(
                coveredActionIndices);

            baseline = null;
            verification = null;
            reconciliation = null;
            ResetPendingMismatch();

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

    private PlanExecutionCoordinatorStatus GetCompletedStatus() =>
        session?.Plan.CapacityBlocked > 0
            ? PlanExecutionCoordinatorStatus.WaitingForCapacity
            : PlanExecutionCoordinatorStatus.Complete;

    private void EnsureBaseline(
        PlannerAction action,
        InventoryIndex inventoryIndex)
    {
        if (session is null)
            return;

        if (baseline is not null &&
            baseline.ActionIndex ==
                session.CurrentActionIndex)
        {
            return;
        }

        baseline =
            verifier.CaptureBaseline(
                session.CurrentActionIndex,
                action,
                inventoryIndex);

        verification = null;
        reconciliation = null;
        ResetPendingMismatch();
    }

    private bool IsMismatchObservationSettled(
        PlanExecutionObservation observation)
    {
        var nowMs =
            Environment.TickCount64;

        if (pendingMismatchObservation is null ||
            pendingMismatchObservation.SourceQuantity !=
                observation.SourceQuantity ||
            pendingMismatchObservation.LogicalSourceQuantity !=
                observation.LogicalSourceQuantity ||
            pendingMismatchObservation.DestinationQuantity !=
                observation.DestinationQuantity ||
            pendingMismatchObservation.SourceLayoutFingerprint !=
                observation.SourceLayoutFingerprint)
        {
            pendingMismatchObservation = observation;
            pendingMismatchSinceMs = nowMs;
            return false;
        }

        return nowMs - pendingMismatchSinceMs >=
            MismatchSettleIntervalMs;
    }

    private void ResetPendingMismatch()
    {
        pendingMismatchObservation = null;
        pendingMismatchSinceMs = 0;
    }

    private PlanExecutionCoordinatorSnapshot Snapshot(
        PlanExecutionCoordinatorStatus status)
    {
        PlannerAction? currentAction = null;

        if (session is not null &&
            !session.IsComplete)
        {
            currentAction =
                materializer.Materialize(
                    session.Plan,
                    session.CurrentActionIndex - 1,
                    session.VerifiedActionIndices)
                .Action;
        }

        return new PlanExecutionCoordinatorSnapshot(
            status,
            session,
            currentAction,
            verification,
            reconciliation);
    }
}
