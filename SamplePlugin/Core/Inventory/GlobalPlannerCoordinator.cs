using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FROG.Core.Inventory;

public readonly record struct GlobalPlannerCoordinatorSnapshot(
    bool IsRunning,
    PlannerPlan? Plan,
    GlobalTransferPlannerDiagnostics? Diagnostics,
    string? Error,
    string? ReplanMessage,
    int? ResolverMissingSnapshot,
    DateTime? ResolverSyncSnapshot);

public sealed record GlobalPlannerCompletion(
    PlannerPlan? Plan,
    string? Error,
    bool AutoStartExecution);

/// <summary>
/// Owns the asynchronous lifecycle of the global transfer planner.
/// Input state is snapshotted before work starts so the planner never reads
/// live UI state or a mutating InventoryIndex while searching.
/// </summary>
public sealed class GlobalPlannerCoordinator
{
    private readonly object syncLock = new();
    private readonly ExecutionOrderCompiler executionOrderCompiler;

    public GlobalPlannerCoordinator(
        ExecutionOrderCompiler executionOrderCompiler)
    {
        this.executionOrderCompiler = executionOrderCompiler;
    }

    private Task<PlannerPlan>? runningTask;
    private long generation;
    private PlannerPlan? plan;
    private GlobalTransferPlannerDiagnostics? diagnostics;
    private string? error;
    private string? replanMessage;
    private int? resolverMissingSnapshot;
    private DateTime? resolverSyncSnapshot;
    private GlobalPlannerCompletion? pendingCompletion;

    public GlobalPlannerCoordinatorSnapshot Snapshot
    {
        get
        {
            lock (syncLock)
            {
                return new GlobalPlannerCoordinatorSnapshot(
                    runningTask != null,
                    plan,
                    diagnostics,
                    error,
                    replanMessage,
                    resolverMissingSnapshot,
                    resolverSyncSnapshot);
            }
        }
    }

    public bool TryStart(
        RequirementSet requirementSet,
        ResolutionPolicy resolutionPolicy,
        ulong mainCharacterId,
        ulong currentCharacterId,
        IReadOnlyList<InventoryItemSnapshot> inventoryItems,
        OptimizationSettings optimizationSettings,
        DateTime? inventorySyncAtUtc,
        int? resolverMissing,
        string? replanMessage,
        bool autoStartExecution)
    {
        if (mainCharacterId == 0 ||
            currentCharacterId == 0)
        {
            return false;
        }

        var requirementSetSnapshot =
            CloneRequirements(
                requirementSet);

        var requiredItemIds =
            requirementSetSnapshot.Requirements
                .Select(requirement =>
                    requirement.BaseItemId)
                .ToHashSet();

        var plannerItems =
            inventoryItems
                .Where(item =>
                    requiredItemIds.Contains(
                        item.BaseItemId))
                .ToList();

        var stateSnapshot =
            new PlannerState(
                mainCharacterId,
                currentCharacterId,
                plannerItems);

        var executionOrderSnapshot =
            executionOrderCompiler.Capture(
                plannerItems);

        var resolutionPolicySnapshot =
            new ResolutionPolicy(
                resolutionPolicy.Sources.ToList(),
                mainCharacterId);

        var optimizationSettingsSnapshot =
            new OptimizationSettings(
                optimizationSettings.Criteria.ToList());

        var planner =
            new GlobalTransferPlanner();

        lock (syncLock)
        {
            if (runningTask != null)
                return false;

            plan = null;
            error = null;
            diagnostics =
                planner.Diagnostics;
            this.replanMessage =
                replanMessage;
            resolverMissingSnapshot =
                replanMessage == null
                    ? resolverMissing
                    : null;
            resolverSyncSnapshot =
                inventorySyncAtUtc;
            pendingCompletion = null;

            var runGeneration =
                ++generation;

            var plannerTask =
                Task.Run(
                    () =>
                    {
                        var planned =
                            planner.Plan(
                                requirementSetSnapshot,
                                stateSnapshot,
                                resolutionPolicySnapshot,
                                optimizationSettingsSnapshot);

                        return executionOrderSnapshot.Compile(
                            planned);
                    });

            runningTask =
                plannerTask;

            _ = CompletePlannerAsync(
                plannerTask,
                runGeneration,
                autoStartExecution);
        }

        return true;
    }

    public bool TryConsumeCompletion(
        out GlobalPlannerCompletion completion)
    {
        lock (syncLock)
        {
            if (pendingCompletion is null)
            {
                completion = null!;
                return false;
            }

            completion = pendingCompletion;
            pendingCompletion = null;
            return true;
        }
    }

    public void ClearResult()
    {
        lock (syncLock)
        {
            generation++;

            plan = null;
            error = null;
            replanMessage = null;
            resolverMissingSnapshot = null;
            resolverSyncSnapshot = null;
            pendingCompletion = null;

            if (runningTask == null)
                diagnostics = null;
        }
    }

    private async Task CompletePlannerAsync(
        Task<PlannerPlan> plannerTask,
        long runGeneration,
        bool autoStartExecution)
    {
        PlannerPlan? completedPlan = null;
        string? completedError = null;

        try
        {
            completedPlan =
                await plannerTask;
        }
        catch (Exception ex)
        {
            completedError =
                ex.Message;
        }

        lock (syncLock)
        {
            if (!ReferenceEquals(
                    runningTask,
                    plannerTask))
            {
                return;
            }

            runningTask = null;

            if (runGeneration != generation)
                return;

            plan = completedPlan;
            error = completedError;
            pendingCompletion =
                new GlobalPlannerCompletion(
                    completedPlan,
                    completedError,
                    autoStartExecution);
        }
    }

    private static RequirementSet CloneRequirements(
        RequirementSet requirementSet)
    {
        var snapshot =
            new RequirementSet(
                requirementSet.Name);

        foreach (var requirement in requirementSet.Requirements)
        {
            snapshot.Add(
                requirement);
        }

        return snapshot;
    }
}
