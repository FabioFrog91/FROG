using System;

namespace FROG.Core.Inventory;

public readonly record struct PlanExecutionRuntimeSnapshot(
    PlanExecutionCoordinatorStatus Status,
    PlanExecutionSession? Session,
    PlannerAction? CurrentAction,
    PlanExecutionVerificationResult? Verification,
    PlanExecutionReconciliationResult? Reconciliation,
    bool IsReplanning,
    string? Error);

/// <summary>
/// Drives execution independently from UI rendering.
///
/// The framework tick observes real InventoryIndex changes, advances verified
/// actions automatically, requests residual replans when reconciliation
/// detects a variance, and invalidates stale residual plans only when their
/// remaining actions are no longer physically executable.
/// </summary>
public sealed class PlanExecutionRuntime
{
    private readonly Plugin plugin;
    private readonly PlannerSourceBuilder plannerSourceBuilder;
    private readonly GlobalPlannerCoordinator globalPlannerCoordinator;
    private readonly PlanExecutionPlanGuard planGuard;
    private readonly PlanExecutionCoordinator executionCoordinator = new();

    private RequirementSet? requirementSet;
    private OptimizationSettings? optimizationSettings;
    private bool isReplanning;
    private string? error;

    private PlanExecutionRuntimeSnapshot snapshot =
        new(
            PlanExecutionCoordinatorStatus.Idle,
            null,
            null,
            null,
            null,
            false,
            null);

    public event Action? SessionStarted;

    public PlanExecutionRuntimeSnapshot Snapshot =>
        snapshot;

    public PlanExecutionRuntime(
        Plugin plugin,
        PlannerSourceBuilder plannerSourceBuilder,
        GlobalPlannerCoordinator globalPlannerCoordinator,
        PlanExecutionPlanGuard planGuard)
    {
        this.plugin = plugin;
        this.plannerSourceBuilder = plannerSourceBuilder;
        this.globalPlannerCoordinator = globalPlannerCoordinator;
        this.planGuard = planGuard;
    }

    public void Start(
        PlannerPlan plan,
        RequirementSet requirements,
        OptimizationSettings settings)
    {
        requirementSet =
            CloneRequirements(
                requirements);

        optimizationSettings =
            new OptimizationSettings(
                settings.Criteria);

        isReplanning = false;
        error = null;

        var currentCharacterId =
            Plugin.PlayerState.IsLoaded
                ? Plugin.PlayerState.ContentId
                : 0;

        var validity =
            planGuard.ValidateRemaining(
                plan,
                firstActionIndex: 0,
                currentCharacterId,
                plugin.InventoryIndex.Items);

        if (!validity.IsValid)
        {
            executionCoordinator.Clear();

            if (TryStartPlanRefresh(
                    plan,
                    currentCharacterId,
                    $"REPLAN ESECUZIONE: il piano precedente non è più valido rispetto allo stato reale. {validity.Message}"))
            {
                RefreshSnapshot(
                    PlanExecutionCoordinatorStatus.ReplanRequired);

                return;
            }

            error =
                $"Il piano precedente non è più valido. {validity.Message}";

            RefreshSnapshot(
                PlanExecutionCoordinatorStatus.ReplanRequired);

            return;
        }

        executionCoordinator.Start(
            plan);

        RefreshSnapshot(
            PlanExecutionCoordinatorStatus.Pending);

        SessionStarted?.Invoke();
    }

    public void Clear()
    {
        executionCoordinator.Clear();
        requirementSet = null;
        optimizationSettings = null;
        isReplanning = false;
        error = null;

        snapshot =
            new PlanExecutionRuntimeSnapshot(
                PlanExecutionCoordinatorStatus.Idle,
                null,
                null,
                null,
                null,
                false,
                null);
    }

    public void ResetProgress()
    {
        executionCoordinator.Reset();
        isReplanning = false;
        error = null;

        RefreshSnapshot(
            executionCoordinator.Session is null
                ? PlanExecutionCoordinatorStatus.Idle
                : PlanExecutionCoordinatorStatus.Pending);
    }

    public void Update(
        ulong currentCharacterId)
    {
        ApplyPlannerCompletion();

        var session =
            executionCoordinator.Session;

        if (session is null)
        {
            RefreshSnapshot(
                PlanExecutionCoordinatorStatus.Idle);

            return;
        }

        if (isReplanning)
        {
            RefreshSnapshot(
                PlanExecutionCoordinatorStatus.ReplanRequired);

            return;
        }

        var execution =
            executionCoordinator.Update(
                plugin.InventoryIndex,
                currentCharacterId);

        snapshot =
            new PlanExecutionRuntimeSnapshot(
                execution.Status,
                execution.Session,
                execution.CurrentAction,
                execution.Verification,
                execution.Reconciliation,
                false,
                error);

        if (execution.Status ==
                PlanExecutionCoordinatorStatus.ReplanRequired &&
            execution.Reconciliation is not null)
        {
            TryStartResidualReplan(
                execution.Reconciliation,
                currentCharacterId);

            return;
        }

        if (execution.Status ==
            PlanExecutionCoordinatorStatus.Verified)
        {
            TryInvalidateStaleRemainingPlan(
                execution.Session,
                currentCharacterId);
        }
    }

    private void ApplyPlannerCompletion()
    {
        if (!globalPlannerCoordinator.TryConsumeCompletion(
                out var completion))
        {
            return;
        }

        if (!completion.AutoStartExecution)
            return;

        isReplanning = false;

        if (completion.Plan is null)
        {
            error =
                completion.Error ??
                "Il ricalcolo del piano non ha prodotto un risultato.";

            RefreshSnapshot(
                PlanExecutionCoordinatorStatus.ReplanRequired);

            return;
        }

        error = null;

        executionCoordinator.Start(
            completion.Plan);

        RefreshSnapshot(
            PlanExecutionCoordinatorStatus.Pending);

        SessionStarted?.Invoke();
    }

    private void TryInvalidateStaleRemainingPlan(
        PlanExecutionSession? session,
        ulong currentCharacterId)
    {
        if (session is null ||
            session.IsComplete ||
            currentCharacterId == 0)
        {
            return;
        }

        var firstActionIndex =
            Math.Max(
                0,
                session.CurrentActionIndex - 1);

        var validity =
            planGuard.ValidateRemaining(
                session.Plan,
                firstActionIndex,
                currentCharacterId,
                plugin.InventoryIndex.Items);

        if (validity.IsValid)
            return;

        TryStartPlanRefresh(
            session.Plan,
            currentCharacterId,
            $"REPLAN ESECUZIONE: lo stato reale ha invalidato il piano residuo. {validity.Message}");
    }

    private void TryStartResidualReplan(
        PlanExecutionReconciliationResult reconciliation,
        ulong currentCharacterId)
    {
        var previousPlan =
            executionCoordinator.Session?.Plan;

        if (previousPlan is null)
            return;

        var variance =
            reconciliation.VarianceQuantity;

        var varianceText =
            variance switch
            {
                > 0 => $"+{variance}",
                < 0 => variance.ToString(),
                _ => "0"
            };

        var replanMessage =
            $"REPLAN ESECUZIONE: previsto {reconciliation.PlannedQuantity}, " +
            $"trasferimento confermato {reconciliation.ObservedTransferredQuantity} " +
            $"(varianza {varianceText}), " +
            $"delta source -{reconciliation.SourceDecrease}, " +
            $"delta destination +{reconciliation.DestinationIncrease}. " +
            $"Piano residuo ricalcolato dallo stato reale.";

        TryStartPlanRefresh(
            previousPlan,
            currentCharacterId,
            replanMessage);
    }

    private bool TryStartPlanRefresh(
        PlannerPlan previousPlan,
        ulong currentCharacterId,
        string replanMessage)
    {
        if (requirementSet is null ||
            optimizationSettings is null ||
            currentCharacterId == 0 ||
            globalPlannerCoordinator.Snapshot.IsRunning)
        {
            return false;
        }

        var mainCharacterId =
            previousPlan.InitialState.MainCharacterId;

        if (mainCharacterId == 0)
            return false;

        var currentItems =
            plugin.InventoryIndex.Items;

        var sources =
            plannerSourceBuilder.Build(
                currentItems,
                mainCharacterId);

        var resolutionPolicy =
            new ResolutionPolicy(
                sources,
                mainCharacterId);

        var started =
            globalPlannerCoordinator.TryStart(
                requirementSet,
                resolutionPolicy,
                mainCharacterId,
                currentCharacterId,
                currentItems,
                optimizationSettings,
                plugin.LastSyncAtUtc,
                resolverMissing: null,
                replanMessage: replanMessage,
                autoStartExecution: true);

        if (!started)
            return false;

        isReplanning = true;
        error = null;

        snapshot =
            snapshot with
            {
                Status =
                    PlanExecutionCoordinatorStatus.ReplanRequired,
                IsReplanning = true
            };

        return true;
    }

    private void RefreshSnapshot(
        PlanExecutionCoordinatorStatus status)
    {
        var session =
            executionCoordinator.Session;

        snapshot =
            new PlanExecutionRuntimeSnapshot(
                status,
                session,
                session?.CurrentAction,
                snapshot.Verification,
                snapshot.Reconciliation,
                isReplanning,
                error);
    }

    private static RequirementSet CloneRequirements(
        RequirementSet source)
    {
        var clone =
            new RequirementSet(
                source.Name);

        foreach (var requirement in source.Requirements)
        {
            clone.Add(
                requirement);
        }

        return clone;
    }
}
