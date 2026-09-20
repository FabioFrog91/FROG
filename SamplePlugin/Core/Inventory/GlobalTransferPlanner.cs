using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace FROG.Core.Inventory;

/// <summary>
/// Stable public entry point for global transfer planning.
///
/// The optimization engine intentionally operates on allocation/source choices,
/// while concrete routing and physical action ordering are compiled afterwards.
/// Keeping this facade small preserves a modular boundary for future providers,
/// execution, verification and reconciliation layers.
/// </summary>
public sealed class GlobalTransferPlanner
{
    private readonly PlannerActionValidator actionValidator = new();
    private readonly PlannerPlanEvaluator planEvaluator = new();

    public GlobalTransferPlannerDiagnostics Diagnostics { get; } = new();

    public PlannerPlan Plan(
        RequirementSet requirements,
        PlannerState initialState,
        ResolutionPolicy resolutionPolicy,
        OptimizationSettings optimizationSettings)
    {
        var planner =
            new GlobalAllocationPlanner(
                actionValidator,
                planEvaluator,
                Diagnostics);

        return planner.Plan(
            requirements,
            initialState,
            resolutionPolicy,
            optimizationSettings);
    }
}


public sealed class GlobalTransferPlannerDiagnostics
{
    private const long MemorySampleInterval = 256;

    private readonly Stopwatch stopwatch = new();
    private readonly object actionBreakdownLock = new();
    private readonly Dictionary<uint, long> generatedActionsByItem = new();
    private readonly Dictionary<PlannerActionSourceKey, long> generatedActionsBySource = new();

    private long searchCalls;
    private long uniqueStates;
    private long dominatedStates;
    private long memoStates;
    private long memoEntries;
    private long generatedActions;
    private long appliedActions;
    private long moveActionsGenerated;
    private long switchActionsGenerated;
    private long expandedStates;
    private long pathCyclePrunes;
    private long deadEnds;
    private long branchingTotal;
    private long maxBranching;
    private long statesWithOneAction;
    private long statesWithTwoActions;
    private long statesWithThreeToFiveActions;
    private long statesWithSixToTenActions;
    private long statesWithMoreThanTenActions;
    private long maxDepth;
    private long elapsedMilliseconds;
    private long managedMemoryStartBytes;
    private long managedMemoryBytes;
    private long peakManagedMemoryBytes;
    private long managedMemoryEndBytes;
    private long allocatedBytes;
    private long startAllocatedBytes;

    internal void Reset()
    {
        stopwatch.Reset();

        Interlocked.Exchange(
            ref searchCalls,
            0);

        Interlocked.Exchange(
            ref uniqueStates,
            0);

        Interlocked.Exchange(
            ref dominatedStates,
            0);

        Interlocked.Exchange(
            ref memoStates,
            0);

        Interlocked.Exchange(
            ref memoEntries,
            0);

        Interlocked.Exchange(
            ref generatedActions,
            0);

        Interlocked.Exchange(
            ref appliedActions,
            0);

        Interlocked.Exchange(
            ref moveActionsGenerated,
            0);

        Interlocked.Exchange(
            ref switchActionsGenerated,
            0);

        Interlocked.Exchange(
            ref expandedStates,
            0);

        Interlocked.Exchange(
            ref pathCyclePrunes,
            0);

        Interlocked.Exchange(
            ref deadEnds,
            0);

        Interlocked.Exchange(
            ref branchingTotal,
            0);

        Interlocked.Exchange(
            ref maxBranching,
            0);

        Interlocked.Exchange(
            ref statesWithOneAction,
            0);

        Interlocked.Exchange(
            ref statesWithTwoActions,
            0);

        Interlocked.Exchange(
            ref statesWithThreeToFiveActions,
            0);

        Interlocked.Exchange(
            ref statesWithSixToTenActions,
            0);

        Interlocked.Exchange(
            ref statesWithMoreThanTenActions,
            0);

        lock (actionBreakdownLock)
        {
            generatedActionsByItem.Clear();
            generatedActionsBySource.Clear();
        }

        Interlocked.Exchange(
            ref maxDepth,
            0);

        Interlocked.Exchange(
            ref elapsedMilliseconds,
            0);

        Interlocked.Exchange(
            ref managedMemoryStartBytes,
            0);

        Interlocked.Exchange(
            ref managedMemoryBytes,
            0);

        Interlocked.Exchange(
            ref peakManagedMemoryBytes,
            0);

        Interlocked.Exchange(
            ref managedMemoryEndBytes,
            0);

        Interlocked.Exchange(
            ref allocatedBytes,
            0);

        Interlocked.Exchange(
            ref startAllocatedBytes,
            0);
    }

    internal void Start()
    {
        Interlocked.Exchange(
            ref startAllocatedBytes,
            GC.GetAllocatedBytesForCurrentThread());

        var startManagedMemory =
            GC.GetTotalMemory(
                forceFullCollection: false);

        Interlocked.Exchange(
            ref managedMemoryStartBytes,
            startManagedMemory);

        Interlocked.Exchange(
            ref managedMemoryBytes,
            startManagedMemory);

        Interlocked.Exchange(
            ref peakManagedMemoryBytes,
            startManagedMemory);

        stopwatch.Restart();

        ObserveRuntime();
    }

    internal void RecordSearch(
        int depth)
    {
        var calls =
            Interlocked.Increment(
                ref searchCalls);

        UpdateMaximum(
            ref maxDepth,
            depth);

        if (calls % MemorySampleInterval == 0)
        {
            ObserveRuntime();
        }
    }

    internal void RecordDominatedState()
    {
        Interlocked.Increment(
            ref dominatedStates);
    }

    internal void RecordMemoRegistration(
        bool newMemoState,
        int memoEntryDelta)
    {
        if (newMemoState)
        {
            Interlocked.Increment(
                ref uniqueStates);

            Interlocked.Increment(
                ref memoStates);
        }

        Interlocked.Add(
            ref memoEntries,
            memoEntryDelta);
    }

    internal void RecordGeneratedAction(
        PlannerAction action)
    {
        Interlocked.Increment(
            ref generatedActions);

        if (action.Type == PlannerActionType.SwitchCharacter)
        {
            Interlocked.Increment(
                ref switchActionsGenerated);

            return;
        }

        Interlocked.Increment(
            ref moveActionsGenerated);

        lock (actionBreakdownLock)
        {
            generatedActionsByItem.TryGetValue(
                action.BaseItemId,
                out var itemCount);

            generatedActionsByItem[action.BaseItemId] =
                itemCount + 1;

            if (action.Source is not null)
            {
                var sourceKey =
                    new PlannerActionSourceKey(
                        action.Source.Storage,
                        action.Source.OwnerId,
                        action.Source.Container,
                        action.Source.ParentCharacterId);

                generatedActionsBySource.TryGetValue(
                    sourceKey,
                    out var sourceCount);

                generatedActionsBySource[sourceKey] =
                    sourceCount + 1;
            }
        }
    }

    internal void RecordExpansion(
        int generatedCount,
        int appliedCount)
    {
        Interlocked.Increment(
            ref expandedStates);

        Interlocked.Add(
            ref branchingTotal,
            generatedCount);

        UpdateMaximum(
            ref maxBranching,
            generatedCount);

        if (appliedCount == 0)
        {
            Interlocked.Increment(
                ref deadEnds);
        }

        if (generatedCount == 1)
        {
            Interlocked.Increment(
                ref statesWithOneAction);
        }
        else if (generatedCount == 2)
        {
            Interlocked.Increment(
                ref statesWithTwoActions);
        }
        else if (generatedCount >= 3 &&
                 generatedCount <= 5)
        {
            Interlocked.Increment(
                ref statesWithThreeToFiveActions);
        }
        else if (generatedCount >= 6 &&
                 generatedCount <= 10)
        {
            Interlocked.Increment(
                ref statesWithSixToTenActions);
        }
        else if (generatedCount > 10)
        {
            Interlocked.Increment(
                ref statesWithMoreThanTenActions);
        }
    }

    internal void RecordPathCyclePrune()
    {
        Interlocked.Increment(
            ref pathCyclePrunes);
    }

    internal void RecordAppliedAction()
    {
        Interlocked.Increment(
            ref appliedActions);
    }

    internal void Complete(
        int finalMemoStates,
        int finalMemoEntries)
    {
        ObserveRuntime();

        Interlocked.Exchange(
            ref managedMemoryEndBytes,
            Interlocked.Read(
                ref managedMemoryBytes));

        stopwatch.Stop();

        Interlocked.Exchange(
            ref elapsedMilliseconds,
            stopwatch.ElapsedMilliseconds);

        Interlocked.Exchange(
            ref memoStates,
            finalMemoStates);

        Interlocked.Exchange(
            ref uniqueStates,
            finalMemoStates);

        Interlocked.Exchange(
            ref memoEntries,
            finalMemoEntries);
    }

    public GlobalTransferPlannerDiagnosticsSnapshot Snapshot()
    {
        PlannerActionItemDiagnostic[] topItems;
        PlannerActionSourceDiagnostic[] topSources;

        lock (actionBreakdownLock)
        {
            topItems =
                generatedActionsByItem
                    .OrderByDescending(pair =>
                        pair.Value)
                    .ThenBy(pair =>
                        pair.Key)
                    .Take(10)
                    .Select(pair =>
                        new PlannerActionItemDiagnostic(
                            pair.Key,
                            pair.Value))
                    .ToArray();

            topSources =
                generatedActionsBySource
                    .OrderByDescending(pair =>
                        pair.Value)
                    .ThenBy(pair =>
                        pair.Key.Storage)
                    .ThenBy(pair =>
                        pair.Key.OwnerId)
                    .ThenBy(pair =>
                        pair.Key.Container)
                    .Take(10)
                    .Select(pair =>
                        new PlannerActionSourceDiagnostic(
                            pair.Key.Storage,
                            pair.Key.OwnerId,
                            pair.Key.Container,
                            pair.Key.ParentCharacterId,
                            pair.Value))
                    .ToArray();
        }

        return new GlobalTransferPlannerDiagnosticsSnapshot(
            SearchCalls:
                Interlocked.Read(
                    ref searchCalls),
            UniqueStates:
                Interlocked.Read(
                    ref uniqueStates),
            DominatedStates:
                Interlocked.Read(
                    ref dominatedStates),
            MemoStates:
                Interlocked.Read(
                    ref memoStates),
            MemoEntries:
                Interlocked.Read(
                    ref memoEntries),
            GeneratedActions:
                Interlocked.Read(
                    ref generatedActions),
            AppliedActions:
                Interlocked.Read(
                    ref appliedActions),
            MoveActionsGenerated:
                Interlocked.Read(
                    ref moveActionsGenerated),
            SwitchActionsGenerated:
                Interlocked.Read(
                    ref switchActionsGenerated),
            ExpandedStates:
                Interlocked.Read(
                    ref expandedStates),
            PathCyclePrunes:
                Interlocked.Read(
                    ref pathCyclePrunes),
            DeadEnds:
                Interlocked.Read(
                    ref deadEnds),
            BranchingTotal:
                Interlocked.Read(
                    ref branchingTotal),
            MaxBranching:
                Interlocked.Read(
                    ref maxBranching),
            StatesWithOneAction:
                Interlocked.Read(
                    ref statesWithOneAction),
            StatesWithTwoActions:
                Interlocked.Read(
                    ref statesWithTwoActions),
            StatesWithThreeToFiveActions:
                Interlocked.Read(
                    ref statesWithThreeToFiveActions),
            StatesWithSixToTenActions:
                Interlocked.Read(
                    ref statesWithSixToTenActions),
            StatesWithMoreThanTenActions:
                Interlocked.Read(
                    ref statesWithMoreThanTenActions),
            TopItems:
                topItems,
            TopSources:
                topSources,
            MaxDepth:
                Interlocked.Read(
                    ref maxDepth),
            ElapsedMilliseconds:
                Interlocked.Read(
                    ref elapsedMilliseconds),
            ManagedMemoryStartBytes:
                Interlocked.Read(
                    ref managedMemoryStartBytes),
            ManagedMemoryBytes:
                Interlocked.Read(
                    ref managedMemoryBytes),
            PeakManagedMemoryBytes:
                Interlocked.Read(
                    ref peakManagedMemoryBytes),
            ManagedMemoryEndBytes:
                Interlocked.Read(
                    ref managedMemoryEndBytes),
            AllocatedBytes:
                Interlocked.Read(
                    ref allocatedBytes));
    }

    private void ObserveRuntime()
    {
        Interlocked.Exchange(
            ref elapsedMilliseconds,
            stopwatch.ElapsedMilliseconds);

        var managedMemory =
            GC.GetTotalMemory(
                forceFullCollection: false);

        Interlocked.Exchange(
            ref managedMemoryBytes,
            managedMemory);

        UpdateMaximum(
            ref peakManagedMemoryBytes,
            managedMemory);

        var startAllocated =
            Interlocked.Read(
                ref startAllocatedBytes);

        var currentAllocated =
            GC.GetAllocatedBytesForCurrentThread();

        Interlocked.Exchange(
            ref allocatedBytes,
            Math.Max(
                0,
                currentAllocated - startAllocated));
    }

    private readonly record struct PlannerActionSourceKey(
        StorageType Storage,
        ulong OwnerId,
        uint Container,
        ulong ParentCharacterId);

    private static void UpdateMaximum(
        ref long target,
        long candidate)
    {
        while (true)
        {
            var current =
                Interlocked.Read(
                    ref target);

            if (candidate <= current)
                return;

            if (Interlocked.CompareExchange(
                    ref target,
                    candidate,
                    current) == current)
            {
                return;
            }
        }
    }
}

public sealed record PlannerActionItemDiagnostic(
    uint BaseItemId,
    long GeneratedActions);

public sealed record PlannerActionSourceDiagnostic(
    StorageType Storage,
    ulong OwnerId,
    uint Container,
    ulong ParentCharacterId,
    long GeneratedActions);

public sealed record GlobalTransferPlannerDiagnosticsSnapshot(
    long SearchCalls,
    long UniqueStates,
    long DominatedStates,
    long MemoStates,
    long MemoEntries,
    long GeneratedActions,
    long AppliedActions,
    long MoveActionsGenerated,
    long SwitchActionsGenerated,
    long ExpandedStates,
    long PathCyclePrunes,
    long DeadEnds,
    long BranchingTotal,
    long MaxBranching,
    long StatesWithOneAction,
    long StatesWithTwoActions,
    long StatesWithThreeToFiveActions,
    long StatesWithSixToTenActions,
    long StatesWithMoreThanTenActions,
    IReadOnlyList<PlannerActionItemDiagnostic> TopItems,
    IReadOnlyList<PlannerActionSourceDiagnostic> TopSources,
    long MaxDepth,
    long ElapsedMilliseconds,
    long ManagedMemoryStartBytes,
    long ManagedMemoryBytes,
    long PeakManagedMemoryBytes,
    long ManagedMemoryEndBytes,
    long AllocatedBytes);
