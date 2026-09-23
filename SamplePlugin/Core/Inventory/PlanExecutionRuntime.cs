using System;
using System.Linq;

namespace FROG.Core.Inventory;

public readonly record struct PlanExecutionRuntimeSnapshot(
    PlanExecutionCoordinatorStatus Status,
    PlanExecutionSession? Session,
    ExecutionInstruction? CurrentInstruction,
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
    private const int CapacityPollIntervalMs = 250;

    private readonly Plugin plugin;
    private readonly PlannerSourceBuilder plannerSourceBuilder;
    private readonly GlobalPlannerCoordinator globalPlannerCoordinator;
    private readonly PlanExecutionPlanGuard planGuard;
    private readonly PlanExecutionCoordinator executionCoordinator;

    private RequirementSet? requirementSet;
    private OptimizationSettings? optimizationSettings;
    private bool isReplanning;
    private string? error;
    private long lastCapacityPollAtMs;

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
        PlanExecutionPlanGuard planGuard,
        ExecutionOrderCompiler executionOrderCompiler)
    {
        this.plugin = plugin;
        this.plannerSourceBuilder = plannerSourceBuilder;
        this.globalPlannerCoordinator = globalPlannerCoordinator;
        this.planGuard = planGuard;
        executionCoordinator =
            new PlanExecutionCoordinator(
                executionOrderCompiler);
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
        lastCapacityPollAtMs = 0;

        var currentCharacterId =
            Plugin.PlayerState.IsLoaded
                ? Plugin.PlayerState.ContentId
                : 0;

        TryBeginExecutionPlan(
            plan,
            currentCharacterId);
    }

    public void Clear()
    {
        executionCoordinator.Clear();
        requirementSet = null;
        optimizationSettings = null;
        isReplanning = false;
        error = null;
        lastCapacityPollAtMs = 0;

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
        lastCapacityPollAtMs = 0;

        RefreshSnapshot(
            executionCoordinator.Session is null
                ? PlanExecutionCoordinatorStatus.Idle
                : GetInitialStatus(
                    executionCoordinator.Session.Plan));
    }

    public void Update(
        ulong currentCharacterId)
    {
        ApplyPlannerCompletion(
            currentCharacterId);

        if (isReplanning)
        {
            RefreshSnapshot(
                PlanExecutionCoordinatorStatus.ReplanRequired);

            return;
        }

        var session =
            executionCoordinator.Session;

        if (session is null)
        {
            RefreshSnapshot(
                PlanExecutionCoordinatorStatus.Idle);

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
                execution.CurrentInstruction,
                execution.Verification,
                execution.Reconciliation,
                false,
                error);

        if (execution.Status ==
            PlanExecutionCoordinatorStatus.ReplanRequired)
        {
            if (execution.Reconciliation is not null)
            {
                TryStartResidualReplan(
                    execution.Reconciliation,
                    currentCharacterId);
            }
            else if (!string.IsNullOrWhiteSpace(
                         execution.ReplanReason))
            {
                var previousPlan =
                    execution.Session?.Plan;

                if (previousPlan is not null)
                {
                    TryStartPlanRefresh(
                        previousPlan,
                        currentCharacterId,
                        $"REPLAN ESECUZIONE: {execution.ReplanReason}");
                }
            }

            return;
        }

        if (execution.Status ==
            PlanExecutionCoordinatorStatus.WaitingForCapacity)
        {
            TryStartCapacityReplan(
                execution.Session,
                currentCharacterId);

            return;
        }

        if (execution.Status ==
            PlanExecutionCoordinatorStatus.Verified)
        {
            TryValidateNextDecision(
                execution.Session,
                currentCharacterId);
        }
    }

    private void ApplyPlannerCompletion(
        ulong currentCharacterId)
    {
        if (!globalPlannerCoordinator.TryConsumeCompletion(
                out var completion))
        {
            return;
        }

        if (!completion.AutoStartExecution ||
            !isReplanning)
        {
            return;
        }

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
        lastCapacityPollAtMs = 0;

        TryBeginExecutionPlan(
            completion.Plan,
            currentCharacterId);
    }

    private bool TryBeginExecutionPlan(
        PlannerPlan plan,
        ulong currentCharacterId)
    {
        var firstDecision =
            plan.Decisions.FirstOrDefault();

        if (firstDecision is not null)
        {
            var validity =
                planGuard.ValidateCurrent(
                    plan,
                    firstDecision,
                    currentCharacterId,
                    plugin.InventoryIndex.Items);

            if (!validity.IsValid)
            {
                executionCoordinator.Clear();

                if (TryStartPlanRefresh(
                        plan,
                        currentCharacterId,
                        $"REPLAN ESECUZIONE: la decisione corrente non è più valida rispetto allo stato reale. {validity.Message}"))
                {
                    RefreshSnapshot(
                        PlanExecutionCoordinatorStatus.ReplanRequired);

                    return false;
                }

                error =
                    $"La decisione corrente non è più valida. {validity.Message}";

                RefreshSnapshot(
                    PlanExecutionCoordinatorStatus.ReplanRequired);

                return false;
            }
        }

        executionCoordinator.Start(
            plan);

        RefreshSnapshot(
            GetInitialStatus(
                plan));

        SessionStarted?.Invoke();

        return true;
    }

    private void TryStartCapacityReplan(
        PlanExecutionSession? session,
        ulong currentCharacterId)
    {
        if (session is null ||
            currentCharacterId == 0)
        {
            return;
        }

        var plan =
            session.Plan;

        if (plan.CapacityBlocks.Count == 0)
            return;

        var nowMs =
            Environment.TickCount64;

        if (lastCapacityPollAtMs != 0 &&
            nowMs - lastCapacityPollAtMs <
            CapacityPollIntervalMs)
        {
            return;
        }

        lastCapacityPollAtMs =
            nowMs;

        var simulatedCapacity =
            plan.FinalState.Capacity.Rebase(
                plugin.InventoryIndex.Items);

        foreach (var capacityBlock in plan.CapacityBlocks)
        {
            if (!simulatedCapacity.TryReserveDestination(
                    capacityBlock.Destination,
                    capacityBlock.BaseItemId,
                    capacityBlock.IsHq,
                    capacityBlock.Quantity,
                    out simulatedCapacity))
            {
                return;
            }
        }

        var requiredSlots =
            plan.CapacityAdvice.Sum(advice =>
                advice.MinimumAdditionalSlots);

        TryStartPlanRefresh(
            plan,
            currentCharacterId,
            $"REPLAN ESECUZIONE: lo spazio osservato ora consente tutti i movimenti bloccati noti ({plan.CapacityBlocked} unità, {requiredSlots} slot minimi). Piano ricalcolato dallo stato reale.");
    }

    private void TryValidateNextDecision(
        PlanExecutionSession? session,
        ulong currentCharacterId)
    {
        if (session is null ||
            session.IsComplete ||
            currentCharacterId == 0 ||
            session.CurrentDecision is null)
        {
            return;
        }

        var validity =
            planGuard.ValidateCurrent(
                session.Plan,
                session.CurrentDecision,
                currentCharacterId,
                plugin.InventoryIndex.Items);

        if (validity.IsValid)
            return;

        TryStartPlanRefresh(
            session.Plan,
            currentCharacterId,
            $"REPLAN ESECUZIONE: la prossima decisione non è più valida rispetto allo stato reale. {validity.Message}");
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
            $"{reconciliation.Message} " +
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
                null,
                snapshot.Verification,
                snapshot.Reconciliation,
                isReplanning,
                error);
    }

    private static PlanExecutionCoordinatorStatus GetInitialStatus(
        PlannerPlan plan) =>
        plan.Decisions.Count == 0 &&
        plan.CapacityBlocked > 0
            ? PlanExecutionCoordinatorStatus.WaitingForCapacity
            : PlanExecutionCoordinatorStatus.Pending;

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
