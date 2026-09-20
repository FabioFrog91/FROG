using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace FROG.Core.Inventory;

public readonly record struct ResolverCoordinatorSnapshot(
    IReadOnlyList<InventorySource>? Sources,
    InventorySourceCatalog? SourceCatalog,
    ResolutionPolicy? ResolutionPolicy,
    TransferPlan? Plan,
    DateTime? SyncAtUtc,
    ulong CharacterId,
    long ComputeCalls,
    double LastElapsedMilliseconds,
    long LastAllocatedBytes,
    long TotalAllocatedBytes,
    long PeakAllocatedBytes)
{
    public bool HasResult =>
        Sources is not null &&
        SourceCatalog is not null &&
        ResolutionPolicy is not null &&
        Plan is not null;
}

/// <summary>
/// Owns resolver source construction, cache validity, transfer-plan computation,
/// and resolver diagnostics. Presentation code consumes Snapshot only.
/// </summary>
public sealed class ResolverCoordinator
{
    private readonly PlannerSourceBuilder plannerSourceBuilder;

    private RequirementSet? cachedRequirementSet;
    private IReadOnlyList<InventorySource>? cachedSources;
    private InventorySourceCatalog? cachedSourceCatalog;
    private ResolutionPolicy? cachedResolutionPolicy;
    private TransferPlan? cachedPlan;
    private DateTime? cachedSyncAtUtc;
    private ulong cachedCharacterId;

    private long computeCalls;
    private double lastElapsedMilliseconds;
    private long lastAllocatedBytes;
    private long totalAllocatedBytes;
    private long peakAllocatedBytes;

    public ResolverCoordinator(
        PlannerSourceBuilder plannerSourceBuilder)
    {
        this.plannerSourceBuilder =
            plannerSourceBuilder;
    }

    public ResolverCoordinatorSnapshot Snapshot =>
        new(
            cachedSources,
            cachedSourceCatalog,
            cachedResolutionPolicy,
            cachedPlan,
            cachedSyncAtUtc,
            cachedCharacterId,
            computeCalls,
            lastElapsedMilliseconds,
            lastAllocatedBytes,
            totalAllocatedBytes,
            peakAllocatedBytes);

    public ResolverCoordinatorSnapshot Resolve(
        RequirementSet requirementSet,
        ulong currentCharacterId,
        InventoryIndex inventoryIndex,
        DateTime? syncAtUtc)
    {
        if (IsCacheValid(
                requirementSet,
                currentCharacterId,
                syncAtUtc))
        {
            return Snapshot;
        }

        var indexItems =
            inventoryIndex.Items;

        var allocatedBefore =
            GC.GetAllocatedBytesForCurrentThread();

        var stopwatch =
            Stopwatch.StartNew();

        var sources =
            plannerSourceBuilder.Build(
                indexItems,
                currentCharacterId);

        if (sources.Count == 0)
        {
            stopwatch.Stop();

            ClearCachedResult();

            RecordDiagnostics(
                stopwatch,
                allocatedBefore);

            return Snapshot;
        }

        var sourceCatalog =
            new InventorySourceCatalog();

        foreach (var source in sources)
        {
            sourceCatalog.Add(
                new SourcePolicy(
                    source,
                    Read: true,
                    Use: true));
        }

        var resolutionPolicy =
            new ResolutionPolicy(
                sources,
                currentCharacterId);

        var planner =
            new TransferPlanner(
                new RequirementResolver());

        var plan =
            planner.Plan(
                requirementSet,
                inventoryIndex,
                sourceCatalog,
                resolutionPolicy);

        stopwatch.Stop();

        cachedRequirementSet =
            requirementSet;

        cachedSources =
            sources;

        cachedSourceCatalog =
            sourceCatalog;

        cachedResolutionPolicy =
            resolutionPolicy;

        cachedPlan =
            plan;

        cachedSyncAtUtc =
            syncAtUtc;

        cachedCharacterId =
            currentCharacterId;

        RecordDiagnostics(
            stopwatch,
            allocatedBefore);

        return Snapshot;
    }

    public void Invalidate(
        bool resetDiagnostics)
    {
        ClearCachedResult();

        if (!resetDiagnostics)
            return;

        computeCalls = 0;
        lastElapsedMilliseconds = 0;
        lastAllocatedBytes = 0;
        totalAllocatedBytes = 0;
        peakAllocatedBytes = 0;
    }

    private bool IsCacheValid(
        RequirementSet requirementSet,
        ulong currentCharacterId,
        DateTime? syncAtUtc) =>
        cachedPlan is not null &&
        cachedSources is not null &&
        cachedSourceCatalog is not null &&
        cachedResolutionPolicy is not null &&
        ReferenceEquals(
            cachedRequirementSet,
            requirementSet) &&
        cachedSyncAtUtc == syncAtUtc &&
        cachedCharacterId == currentCharacterId;

    private void ClearCachedResult()
    {
        cachedRequirementSet = null;
        cachedSources = null;
        cachedSourceCatalog = null;
        cachedResolutionPolicy = null;
        cachedPlan = null;
        cachedSyncAtUtc = null;
        cachedCharacterId = 0;
    }

    private void RecordDiagnostics(
        Stopwatch stopwatch,
        long allocatedBefore)
    {
        computeCalls++;

        lastElapsedMilliseconds =
            stopwatch.Elapsed.TotalMilliseconds;

        lastAllocatedBytes =
            Math.Max(
                0,
                GC.GetAllocatedBytesForCurrentThread() -
                allocatedBefore);

        totalAllocatedBytes +=
            lastAllocatedBytes;

        peakAllocatedBytes =
            Math.Max(
                peakAllocatedBytes,
                lastAllocatedBytes);
    }
}
