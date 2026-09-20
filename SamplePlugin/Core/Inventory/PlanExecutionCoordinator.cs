namespace FROG.Core.Inventory;

public enum PlanExecutionCoordinatorStatus
{
    Idle,
    Pending,
    Executed,
    WaitingForObservation,
    Verified,
    ReplanRequired,
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
    private readonly PlanExecutionVerifier verifier = new();
    private readonly PlanExecutionReconciler reconciler = new();

    private PlanExecutionSession? session;
    private PlanExecutionBaseline? baseline;
    private PlanExecutionVerificationResult? verification;
    private PlanExecutionReconciliationResult? reconciliation;

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
    }

    public void Reset()
    {
        if (session is not null)
            session.Reset();

        baseline = null;
        verification = null;
        reconciliation = null;
    }

    public void Clear()
    {
        session = null;
        baseline = null;
        verification = null;
        reconciliation = null;
    }

    public bool TryMarkCurrentExecuted(
        out PlannerAction? executedAction)
    {
        if (session is null)
        {
            executedAction = null;
            return false;
        }

        return session.TryMarkCurrentExecuted(
            out executedAction);
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
                PlanExecutionCoordinatorStatus.Complete);
        }

        var action =
            session.CurrentAction;

        if (action is null)
        {
            return Snapshot(
                PlanExecutionCoordinatorStatus.Complete);
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

                session.TryMarkCurrentExecuted(
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
            session.TryMarkCurrentVerified();

            baseline = null;
            verification = null;
            reconciliation = null;

            return Snapshot(
                session.IsComplete
                    ? PlanExecutionCoordinatorStatus.Complete
                    : PlanExecutionCoordinatorStatus.Verified);
        }

        return Snapshot(
            verification.Status ==
                PlanExecutionVerificationStatus.WaitingForObservation
                ? PlanExecutionCoordinatorStatus.WaitingForObservation
                : PlanExecutionCoordinatorStatus.Executed);
    }

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
    }

    private PlanExecutionCoordinatorSnapshot Snapshot(
        PlanExecutionCoordinatorStatus status) =>
        new(
            status,
            session,
            session?.CurrentAction,
            verification,
            reconciliation);
}
