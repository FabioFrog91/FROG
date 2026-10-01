using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

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
public sealed class GlobalPlannerCoordinator : IDisposable
{
    private readonly object syncLock = new();
    private readonly ExecutionOrderCompiler executionOrderCompiler;
    private readonly IDataManager dataManager;

    public GlobalPlannerCoordinator(
        ExecutionOrderCompiler executionOrderCompiler,
        IDataManager dataManager)
    {
        this.executionOrderCompiler = executionOrderCompiler;
        this.dataManager = dataManager;
    }

    private Task<PlannerPlan>? runningTask;
    private CancellationTokenSource? runningCancellation;
    private long generation;
    private bool disposed;
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
        lock (syncLock)
        {
            if (disposed ||
                runningTask != null)
            {
                return false;
            }
        }

        if (mainCharacterId == 0 ||
            currentCharacterId == 0)
        {
            return false;
        }

        ForensicTraceRecorder.Record(
            "PLANNER",
            $"TRY_START main={mainCharacterId} current={currentCharacterId} autoStartExecution={autoStartExecution} replanMessage={replanMessage ?? "-"} inventorySync={inventorySyncAtUtc?.ToString("O") ?? "-"}");

        ForensicTraceRecorder.RecordRequirements(
            "GLOBAL_PLANNER_INPUT_REQUIREMENTS",
            requirementSet);

        ForensicTraceRecorder.RecordInventorySnapshot(
            "GLOBAL_PLANNER_INPUT_FULL_INVENTORY",
            inventoryItems);

        ForensicTraceRecorder.Record(
            "PLANNER",
            $"RESOLUTION_SOURCES count={resolutionPolicy.Sources.Count} values={string.Join(" || ", resolutionPolicy.Sources)} criteria={string.Join(",", optimizationSettings.Criteria)}");

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

        ForensicTraceRecorder.RecordInventorySnapshot(
            "GLOBAL_PLANNER_FILTERED_INVENTORY",
            plannerItems);

        var executionOrderSnapshot =
            executionOrderCompiler.Capture(
                plannerItems);

        var resolutionPolicySnapshot =
            new ResolutionPolicy(
                resolutionPolicy.Sources.ToList(),
                mainCharacterId);

        var maximumStacks =
            CaptureMaximumStacks(
                requiredItemIds,
                out var stackSizeError);

        var capacitySnapshot =
            PlannerCapacitySnapshot.Capture(
                inventoryItems,
                resolutionPolicySnapshot.Sources,
                maximumStacks);

        var stateSnapshot =
            new PlannerState(
                mainCharacterId,
                currentCharacterId,
                plannerItems,
                capacitySnapshot);

        var optimizationSettingsSnapshot =
            new OptimizationSettings(
                optimizationSettings.Criteria.ToList());

        var planner =
            new GlobalTransferPlanner();

        lock (syncLock)
        {
            if (disposed ||
                runningTask != null)
            {
                return false;
            }

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

            var plannerCancellation =
                new CancellationTokenSource();

            var cancellationToken =
                plannerCancellation.Token;

            var plannerTask =
                Task.Run(
                    () =>
                    {
                        if (stackSizeError is not null)
                        {
                            throw new InvalidOperationException(
                                stackSizeError);
                        }

                        var planned =
                            planner.Plan(
                                requirementSetSnapshot,
                                stateSnapshot,
                                resolutionPolicySnapshot,
                                optimizationSettingsSnapshot,
                                cancellationToken);

                        cancellationToken.ThrowIfCancellationRequested();

                        return executionOrderSnapshot.Compile(
                            planned);
                    },
                    cancellationToken);

            runningTask =
                plannerTask;

            runningCancellation =
                plannerCancellation;

            _ = CompletePlannerAsync(
                plannerTask,
                plannerCancellation,
                runGeneration,
                autoStartExecution);
        }

        return true;
    }

    private IReadOnlyDictionary<uint, int> CaptureMaximumStacks(
        IReadOnlySet<uint> requiredItemIds,
        out string? error)
    {
        var result =
            new Dictionary<uint, int>();

        var missing =
            new List<uint>();

        var itemSheet =
            dataManager.GetExcelSheet<Item>();

        foreach (var itemId in requiredItemIds.OrderBy(value => value))
        {
            if (!itemSheet.TryGetRow(
                    itemId,
                    out var item) ||
                item.StackSize == 0)
            {
                missing.Add(itemId);
                continue;
            }

            result[itemId] =
                checked((int)item.StackSize);
        }

        error =
            missing.Count == 0
                ? null
                : $"Stack massimo non disponibile per gli item: {string.Join(", ", missing)}.";

        return result;
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

            runningCancellation?.Cancel();

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
        CancellationTokenSource plannerCancellation,
        long runGeneration,
        bool autoStartExecution)
    {
        PlannerPlan? completedPlan = null;
        string? completedError = null;
        var cancelled = false;

        try
        {
            completedPlan =
                await plannerTask;

            if (completedPlan is not null)
            {
                ForensicTraceRecorder.RecordPlan(
                    $"GLOBAL_PLANNER_TASK_COMPLETED generation={runGeneration}",
                    completedPlan);
            }
        }
        catch (OperationCanceledException)
            when (plannerCancellation.IsCancellationRequested)
        {
            cancelled = true;

            ForensicTraceRecorder.Record(
                "PLANNER",
                $"TASK_CANCELLED generation={runGeneration}");
        }
        catch (Exception ex)
        {
            completedError =
                ex.Message;

            ForensicTraceRecorder.Record(
                "PLANNER",
                $"TASK_FAILED generation={runGeneration} error={ex}");
        }

        lock (syncLock)
        {
            if (!ReferenceEquals(
                    runningTask,
                    plannerTask))
            {
                plannerCancellation.Dispose();
                return;
            }

            runningTask = null;

            if (ReferenceEquals(
                    runningCancellation,
                    plannerCancellation))
            {
                runningCancellation = null;
            }

            plannerCancellation.Dispose();

            if (cancelled ||
                runGeneration != generation)
            {
                ForensicTraceRecorder.Record(
                    "PLANNER",
                    $"COMPLETION_DISCARDED runGeneration={runGeneration} currentGeneration={generation} cancelled={cancelled}");

                diagnostics = null;
                return;
            }

            plan = completedPlan;
            error = completedError;
            pendingCompletion =
                new GlobalPlannerCompletion(
                    completedPlan,
                    completedError,
                    autoStartExecution);

            ForensicTraceRecorder.Record(
                "PLANNER",
                $"COMPLETION_ACCEPTED generation={runGeneration} autoStartExecution={autoStartExecution} error={completedError ?? "-"} hasPlan={completedPlan is not null}");
        }
    }

    public void Dispose()
    {
        lock (syncLock)
        {
            if (disposed)
                return;

            disposed = true;
            generation++;
            runningCancellation?.Cancel();

            plan = null;
            error = null;
            replanMessage = null;
            resolverMissingSnapshot = null;
            resolverSyncSnapshot = null;
            pendingCompletion = null;

            if (runningTask == null)
            {
                runningCancellation?.Dispose();
                runningCancellation = null;
                diagnostics = null;
            }
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
