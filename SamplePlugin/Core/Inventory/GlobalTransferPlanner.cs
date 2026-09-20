using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace FROG.Core.Inventory;

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
        Diagnostics.Reset();
        Diagnostics.Start();

        var initialMissing =
            CalculateMissing(
                requirements,
                initialState);

        var initialPlan =
            new PlannerPlan(
                initialState,
                initialState,
                Array.Empty<PlannerAction>(),
                initialMissing == 0
                    ? PlannerPlanResult.Completed
                    : PlannerPlanResult.CompletedWithMissing,
                initialMissing);

        var bestPlan =
            initialPlan;

        var pathStates =
            new HashSet<StateKey>();

        var memo =
            new Dictionary<StateKey, List<MemoEntry>>();

        try
        {
            Search(
                requirements,
                resolutionPolicy,
                optimizationSettings,
                initialState,
                initialPlan,
                pathStates,
                memo,
                0,
                ref bestPlan);

            var missing =
                CalculateMissing(
                    requirements,
                    bestPlan.FinalState);

            return bestPlan.WithResult(
                missing == 0
                    ? PlannerPlanResult.Completed
                    : PlannerPlanResult.CompletedWithMissing,
                missing);
        }
        finally
        {
            Diagnostics.Complete(
                memo.Count,
                memo.Sum(pair =>
                    pair.Value.Count));
        }
    }

    private void Search(
        RequirementSet requirements,
        ResolutionPolicy resolutionPolicy,
        OptimizationSettings optimizationSettings,
        PlannerState state,
        PlannerPlan plan,
        HashSet<StateKey> pathStates,
        Dictionary<StateKey, List<MemoEntry>> memo,
        int depth,
        ref PlannerPlan bestPlan)
    {
        Diagnostics.RecordSearch(depth);

        var stateKey =
            BuildStateKey(state);

        if (!pathStates.Add(stateKey))
            return;

        try
        {
            var planScore =
                planEvaluator.Evaluate(
                    plan,
                    requirements,
                    resolutionPolicy);

            var memoEntry =
                CreateMemoEntry(
                    plan,
                    planScore);

            if (IsDominated(
                    stateKey,
                    memoEntry,
                    memo))
            {
                Diagnostics.RecordDominatedState();
                return;
            }

            RegisterMemoEntry(
                stateKey,
                memoEntry,
                memo,
                out var newMemoState,
                out var memoEntryDelta);

            Diagnostics.RecordMemoRegistration(
                newMemoState,
                memoEntryDelta);

            var currentMissing =
                CalculateMissing(
                    requirements,
                    state);

            var bestMissing =
                CalculateMissing(
                    requirements,
                    bestPlan.FinalState);

            if (currentMissing < bestMissing)
            {
                bestPlan =
                    plan.WithResult(
                        currentMissing == 0
                            ? PlannerPlanResult.Completed
                            : PlannerPlanResult.CompletedWithMissing,
                        currentMissing);
            }
            else if (currentMissing == bestMissing)
            {
                var bestScore =
                    planEvaluator.Evaluate(
                        bestPlan,
                        requirements,
                        resolutionPolicy);

                if (planScore.CompareTo(
                        bestScore,
                        optimizationSettings) < 0)
                {
                    bestPlan =
                        plan.WithResult(
                            currentMissing == 0
                                ? PlannerPlanResult.Completed
                                : PlannerPlanResult.CompletedWithMissing,
                            currentMissing);
                }
            }

            if (currentMissing == 0)
                return;

            foreach (var action in GenerateActions(
                         requirements,
                         state,
                         resolutionPolicy))
            {
                Diagnostics.RecordGeneratedAction();

                if (!actionValidator.CanApply(
                        state,
                        action,
                        out _))
                {
                    continue;
                }

                PlannerState nextState;

                try
                {
                    nextState =
                        Apply(
                            state,
                            action);
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                Diagnostics.RecordAppliedAction();

                var nextPlan =
                    plan.Append(
                        action,
                        nextState);

                Search(
                    requirements,
                    resolutionPolicy,
                    optimizationSettings,
                    nextState,
                    nextPlan,
                    pathStates,
                    memo,
                    depth + 1,
                    ref bestPlan);
            }
        }
        finally
        {
            pathStates.Remove(stateKey);
        }
    }

    private static MemoEntry CreateMemoEntry(
        PlannerPlan plan,
        PlannerPlanScore score)
    {
        var retainerAccesses =
            plan.Actions
                .Where(action =>
                    action.Type == PlannerActionType.Move &&
                    action.Source?.Storage == StorageType.Retainer)
                .Select(action =>
                    $"{action.Source!.OwnerId}:{action.Source.ParentCharacterId}")
                .ToHashSet();

        var transferHops =
            plan.Actions
                .Where(action =>
                    action.Type == PlannerActionType.Move &&
                    action.Source is not null &&
                    action.Destination is not null)
                .Select(action =>
                    BuildTransferHopKey(
                        action.Source!,
                        action.Destination!))
                .ToHashSet();

        return new MemoEntry(
            score.CharacterSwitches,
            score.SourcePriority,
            score.Freshness,
            score.Alphabetical,
            retainerAccesses,
            transferHops);
    }

    private static bool IsDominated(
        StateKey stateKey,
        MemoEntry candidate,
        Dictionary<StateKey, List<MemoEntry>> memo)
    {
        if (!memo.TryGetValue(
                stateKey,
                out var entries))
        {
            return false;
        }

        return entries.Any(existing =>
            Dominates(
                existing,
                candidate));
    }

    private static void RegisterMemoEntry(
        StateKey stateKey,
        MemoEntry candidate,
        Dictionary<StateKey, List<MemoEntry>> memo,
        out bool newMemoState,
        out int memoEntryDelta)
    {
        newMemoState =
            !memo.TryGetValue(
                stateKey,
                out var entries);

        if (newMemoState)
        {
            entries = new List<MemoEntry>();
            memo[stateKey] = entries;
        }

        var removed =
            entries!.RemoveAll(existing =>
                Dominates(
                    candidate,
                    existing));

        entries.Add(candidate);

        memoEntryDelta =
            1 - removed;
    }

    private static bool Dominates(
        MemoEntry left,
        MemoEntry right)
    {
        if (!left.RetainerAccesses.SetEquals(
                right.RetainerAccesses))
        {
            return false;
        }

        if (!left.TransferHops.SetEquals(
                right.TransferHops))
        {
            return false;
        }

        if (!string.Equals(
                left.Alphabetical,
                right.Alphabetical,
                StringComparison.Ordinal))
        {
            return false;
        }

        var noWorse =
            left.CharacterSwitches <= right.CharacterSwitches &&
            left.SourcePriority <= right.SourcePriority &&
            left.Freshness >= right.Freshness;

        if (!noWorse)
            return false;

        return
            left.CharacterSwitches < right.CharacterSwitches ||
            left.SourcePriority < right.SourcePriority ||
            left.Freshness > right.Freshness ||
            IsEquivalent(
                left,
                right);
    }

    private static bool IsEquivalent(
        MemoEntry left,
        MemoEntry right)
    {
        return
            left.CharacterSwitches == right.CharacterSwitches &&
            left.SourcePriority == right.SourcePriority &&
            left.Freshness == right.Freshness &&
            string.Equals(
                left.Alphabetical,
                right.Alphabetical,
                StringComparison.Ordinal) &&
            left.RetainerAccesses.SetEquals(
                right.RetainerAccesses) &&
            left.TransferHops.SetEquals(
                right.TransferHops);
    }

    private static string BuildTransferHopKey(
        InventorySource source,
        InventorySource destination)
    {
        return
            $"{source.Storage}:{source.OwnerId}:{source.Container}" +
            $">" +
            $"{destination.Storage}:{destination.OwnerId}:{destination.Container}";
    }

    private static IEnumerable<PlannerAction> GenerateActions(
        RequirementSet requirements,
        PlannerState state,
        ResolutionPolicy resolutionPolicy)
    {
        foreach (var requirement in requirements.Requirements)
        {
            foreach (var source in resolutionPolicy.Sources)
            {
                if (!IsSourceAccessible(
                        state.CurrentCharacterId,
                        source))
                {
                    continue;
                }

                foreach (var isHq in GetCandidateQualities(
                             requirement,
                             state,
                             source))
                {
                    var available =
                        state.GetQuantity(
                            requirement.BaseItemId,
                            isHq,
                            source);

                    if (available <= 0)
                        continue;

                    var needed =
                        GetNeededFromSource(
                            requirement,
                            state,
                            source,
                            isHq);

                    if (needed <= 0)
                        continue;

                    var destination =
                        GetDestination(
                            state,
                            resolutionPolicy,
                            source);

                    if (destination is null)
                        continue;

                    var quantity =
                        Math.Min(
                            available,
                            needed);

                    yield return PlannerAction.Move(
                        source,
                        destination,
                        requirement.BaseItemId,
                        isHq,
                        quantity);
                }
            }
        }

        foreach (var targetCharacter in GetSwitchTargets(
                     requirements,
                     state,
                     resolutionPolicy))
        {
            yield return PlannerAction.SwitchCharacter(
                state.CurrentCharacterId,
                targetCharacter);
        }
    }

    private static IEnumerable<bool> GetCandidateQualities(
        Requirement requirement,
        PlannerState state,
        InventorySource source)
    {
        var qualities =
            requirement.QualityPolicy switch
            {
                RequirementQualityPolicy.HqOnly =>
                    new[] { true },

                RequirementQualityPolicy.NqOnly =>
                    new[] { false },

                RequirementQualityPolicy.HqFirst =>
                    new[] { true, false },

                RequirementQualityPolicy.NqFirst =>
                    new[] { false, true },

                _ =>
                    new[] { false, true }
            };

        foreach (var isHq in qualities)
        {
            if (state.GetQuantity(
                    requirement.BaseItemId,
                    isHq,
                    source) > 0)
            {
                yield return isHq;
            }
        }
    }

    private static int GetNeededFromSource(
        Requirement requirement,
        PlannerState state,
        InventorySource source,
        bool isHq)
    {
        var includeFreeCompany =
            source.Storage switch
            {
                StorageType.Retainer =>
                    source.ParentCharacterId != state.MainCharacterId,

                StorageType.CharacterInventory =>
                    source.OwnerId != state.MainCharacterId,

                _ =>
                    false
            };

        var includeLocalInventory =
            source.Storage == StorageType.Retainer &&
            source.ParentCharacterId != state.MainCharacterId;

        var hq =
            state.GetMainInventoryQuantity(
                requirement.BaseItemId,
                true);

        var nq =
            state.GetMainInventoryQuantity(
                requirement.BaseItemId,
                false);

        if (includeFreeCompany)
        {
            hq +=
                state.GetFreeCompanyQuantity(
                    requirement.BaseItemId,
                    true);

            nq +=
                state.GetFreeCompanyQuantity(
                    requirement.BaseItemId,
                    false);
        }

        if (includeLocalInventory)
        {
            hq +=
                state.GetCharacterInventoryQuantity(
                    source.ParentCharacterId,
                    requirement.BaseItemId,
                    true);

            nq +=
                state.GetCharacterInventoryQuantity(
                    source.ParentCharacterId,
                    requirement.BaseItemId,
                    false);
        }

        return requirement.QualityPolicy switch
        {
            RequirementQualityPolicy.HqOnly =>
                isHq
                    ? Math.Max(
                        0,
                        requirement.Quantity - hq)
                    : 0,

            RequirementQualityPolicy.NqOnly =>
                !isHq
                    ? Math.Max(
                        0,
                        requirement.Quantity - nq)
                    : 0,

            RequirementQualityPolicy.HqFirst =>
                isHq
                    ? Math.Max(
                        0,
                        requirement.Quantity - hq)
                    : Math.Max(
                        0,
                        requirement.Quantity - hq - nq),

            RequirementQualityPolicy.NqFirst =>
                !isHq
                    ? Math.Max(
                        0,
                        requirement.Quantity - nq)
                    : Math.Max(
                        0,
                        requirement.Quantity - nq - hq),

            RequirementQualityPolicy.Any =>
                Math.Max(
                    0,
                    requirement.Quantity - hq - nq),

            _ =>
                0
        };
    }

    private static bool IsSourceAccessible(
        ulong currentCharacterId,
        InventorySource source)
    {
        return source.Storage switch
        {
            StorageType.CharacterInventory =>
                source.OwnerId == currentCharacterId,

            StorageType.Retainer =>
                source.ParentCharacterId == currentCharacterId,

            StorageType.FreeCompanyChest =>
                true,

            _ =>
                false
        };
    }

    private static InventorySource? GetDestination(
        PlannerState state,
        ResolutionPolicy resolutionPolicy,
        InventorySource source)
    {
        switch (source.Storage)
        {
            case StorageType.Retainer:
                return FindCharacterInventory(
                    resolutionPolicy,
                    source.ParentCharacterId);

            case StorageType.CharacterInventory:
                if (source.OwnerId == state.MainCharacterId)
                    return null;

                return FindFreeCompany(
                    resolutionPolicy,
                    source.OwnerId);

            case StorageType.FreeCompanyChest:
                if (state.CurrentCharacterId != state.MainCharacterId)
                    return null;

                return FindCharacterInventory(
                    resolutionPolicy,
                    state.MainCharacterId);

            default:
                return null;
        }
    }

    private static InventorySource? FindCharacterInventory(
        ResolutionPolicy resolutionPolicy,
        ulong characterId)
    {
        return resolutionPolicy.Sources
            .Where(source =>
                source.Storage == StorageType.CharacterInventory &&
                source.OwnerId == characterId)
            .OrderBy(source =>
                source.Container)
            .FirstOrDefault();
    }

    private static InventorySource? FindFreeCompany(
        ResolutionPolicy resolutionPolicy,
        ulong characterId)
    {
        return resolutionPolicy.Sources
            .Where(source =>
                source.Storage == StorageType.FreeCompanyChest &&
                (source.ParentCharacterId == characterId ||
                 source.ParentCharacterId == 0))
            .OrderBy(source =>
                source.Container)
            .FirstOrDefault();
    }

    private static IEnumerable<ulong> GetSwitchTargets(
        RequirementSet requirements,
        PlannerState state,
        ResolutionPolicy resolutionPolicy)
    {
        if (state.CurrentCharacterId != state.MainCharacterId &&
            HasUsefulFreeCompanyMaterial(
                requirements,
                state))
        {
            yield return state.MainCharacterId;
        }

        foreach (var characterId in resolutionPolicy.Sources
                     .Select(GetCharacterId)
                     .Where(id =>
                         id != 0 &&
                         id != state.CurrentCharacterId &&
                         id != state.MainCharacterId)
                     .Distinct()
                     .OrderBy(id => id))
        {
            if (state.HasVisitedCharacter(
                    characterId))
            {
                continue;
            }

            if (HasUsefulMaterialForCharacter(
                    requirements,
                    state,
                    resolutionPolicy,
                    characterId))
            {
                yield return characterId;
            }
        }
    }

    private static bool HasUsefulMaterialForCharacter(
        RequirementSet requirements,
        PlannerState state,
        ResolutionPolicy resolutionPolicy,
        ulong characterId)
    {
        foreach (var source in resolutionPolicy.Sources)
        {
            if (GetCharacterId(source) != characterId)
                continue;

            if (source.Storage ==
                StorageType.FreeCompanyChest)
            {
                continue;
            }

            foreach (var requirement in requirements.Requirements)
            {
                if (source.Storage ==
                        StorageType.CharacterInventory &&
                    source.OwnerId == characterId &&
                    source.OwnerId == state.MainCharacterId)
                {
                    continue;
                }

                if (GetAllowedQualities(
                        requirement)
                    .Any(isHq =>
                        state.GetQuantity(
                            requirement.BaseItemId,
                            isHq,
                            source) > 0))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasUsefulFreeCompanyMaterial(
        RequirementSet requirements,
        PlannerState state)
    {
        return requirements.Requirements.Any(requirement =>
            GetAllowedQualities(
                    requirement)
                .Any(isHq =>
                    state.GetFreeCompanyQuantity(
                        requirement.BaseItemId,
                        isHq) > 0));
    }

    private static IEnumerable<bool> GetAllowedQualities(
        Requirement requirement)
    {
        return requirement.QualityPolicy switch
        {
            RequirementQualityPolicy.HqOnly =>
                new[] { true },

            RequirementQualityPolicy.NqOnly =>
                new[] { false },

            RequirementQualityPolicy.HqFirst =>
                new[] { true, false },

            RequirementQualityPolicy.NqFirst =>
                new[] { false, true },

            _ =>
                new[] { false, true }
        };
    }

    private static ulong GetCharacterId(
        InventorySource source)
    {
        return source.Storage switch
        {
            StorageType.CharacterInventory =>
                source.OwnerId,

            StorageType.Retainer =>
                source.ParentCharacterId,

            StorageType.FreeCompanyChest =>
                source.ParentCharacterId,

            _ =>
                0
        };
    }

    private static PlannerState Apply(
        PlannerState state,
        PlannerAction action)
    {
        return action.Type switch
        {
            PlannerActionType.Move =>
                state.Move(
                    action.Source!,
                    action.Destination!,
                    action.BaseItemId,
                    action.IsHq,
                    action.Quantity),

            PlannerActionType.SwitchCharacter =>
                state.WithCurrentCharacter(
                    action.ToCharacterId),

            _ =>
                throw new InvalidOperationException(
                    "Unknown planner action.")
        };
    }

    private static int CalculateMissing(
        RequirementSet requirements,
        PlannerState state)
    {
        return requirements.Requirements.Sum(requirement =>
            Math.Max(
                0,
                requirement.Quantity -
                GetSatisfiedMainQuantity(
                    requirement,
                    state)));
    }

    private static int GetSatisfiedMainQuantity(
        Requirement requirement,
        PlannerState state)
    {
        var hq =
            state.GetMainInventoryQuantity(
                requirement.BaseItemId,
                true);

        var nq =
            state.GetMainInventoryQuantity(
                requirement.BaseItemId,
                false);

        return requirement.QualityPolicy switch
        {
            RequirementQualityPolicy.HqOnly =>
                hq,

            RequirementQualityPolicy.NqOnly =>
                nq,

            RequirementQualityPolicy.HqFirst =>
                Math.Min(
                    requirement.Quantity,
                    hq) +
                Math.Min(
                    Math.Max(
                        0,
                        requirement.Quantity - hq),
                    nq),

            RequirementQualityPolicy.NqFirst =>
                Math.Min(
                    requirement.Quantity,
                    nq) +
                Math.Min(
                    Math.Max(
                        0,
                        requirement.Quantity - nq),
                    hq),

            RequirementQualityPolicy.Any =>
                Math.Min(
                    requirement.Quantity,
                    hq + nq),

            _ =>
                0
        };
    }

    private static StateKey BuildStateKey(
        PlannerState state)
    {
        var items =
            state.Items
                .Select(item =>
                    new StateItemKey(
                        item.Storage,
                        item.OwnerId,
                        item.Container,
                        item.Slot,
                        item.BaseItemId,
                        item.IsHq,
                        item.Quantity))
                .ToArray();

        Array.Sort(items);

        var visitedCharacters =
            state.VisitedCharacters
                .ToArray();

        Array.Sort(visitedCharacters);

        return new StateKey(
            state.MainCharacterId,
            state.CurrentCharacterId,
            visitedCharacters,
            items);
    }

    private readonly record struct StateItemKey(
        StorageType Storage,
        ulong OwnerId,
        uint Container,
        int Slot,
        uint BaseItemId,
        bool IsHq,
        int Quantity)
        : IComparable<StateItemKey>
    {
        public int CompareTo(
            StateItemKey other)
        {
            var comparison =
                Storage.CompareTo(
                    other.Storage);

            if (comparison != 0)
                return comparison;

            comparison =
                OwnerId.CompareTo(
                    other.OwnerId);

            if (comparison != 0)
                return comparison;

            comparison =
                Container.CompareTo(
                    other.Container);

            if (comparison != 0)
                return comparison;

            comparison =
                Slot.CompareTo(
                    other.Slot);

            if (comparison != 0)
                return comparison;

            comparison =
                BaseItemId.CompareTo(
                    other.BaseItemId);

            if (comparison != 0)
                return comparison;

            comparison =
                IsHq.CompareTo(
                    other.IsHq);

            if (comparison != 0)
                return comparison;

            return Quantity.CompareTo(
                other.Quantity);
        }
    }

    private sealed class StateKey : IEquatable<StateKey>
    {
        private readonly ulong[] visitedCharacters;
        private readonly StateItemKey[] items;
        private readonly int hashCode;

        public ulong MainCharacterId { get; }
        public ulong CurrentCharacterId { get; }

        public StateKey(
            ulong mainCharacterId,
            ulong currentCharacterId,
            ulong[] visitedCharacters,
            StateItemKey[] items)
        {
            MainCharacterId =
                mainCharacterId;

            CurrentCharacterId =
                currentCharacterId;

            this.visitedCharacters =
                visitedCharacters;

            this.items =
                items;

            var hash =
                new HashCode();

            hash.Add(
                MainCharacterId);

            hash.Add(
                CurrentCharacterId);

            foreach (var characterId in visitedCharacters)
            {
                hash.Add(
                    characterId);
            }

            foreach (var item in items)
            {
                hash.Add(
                    item);
            }

            hashCode =
                hash.ToHashCode();
        }

        public bool Equals(
            StateKey? other)
        {
            if (ReferenceEquals(
                    this,
                    other))
            {
                return true;
            }

            if (other is null ||
                MainCharacterId != other.MainCharacterId ||
                CurrentCharacterId != other.CurrentCharacterId ||
                visitedCharacters.Length != other.visitedCharacters.Length ||
                items.Length != other.items.Length)
            {
                return false;
            }

            for (var i = 0;
                 i < visitedCharacters.Length;
                 i++)
            {
                if (visitedCharacters[i] !=
                    other.visitedCharacters[i])
                {
                    return false;
                }
            }

            for (var i = 0;
                 i < items.Length;
                 i++)
            {
                if (items[i] !=
                    other.items[i])
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(
            object? obj) =>
            obj is StateKey other &&
            Equals(other);

        public override int GetHashCode() =>
            hashCode;
    }

    private sealed class MemoEntry
    {
        public int CharacterSwitches { get; }
        public int SourcePriority { get; }
        public DateTime Freshness { get; }
        public string Alphabetical { get; }
        public HashSet<string> RetainerAccesses { get; }
        public HashSet<string> TransferHops { get; }

        public MemoEntry(
            int characterSwitches,
            int sourcePriority,
            DateTime freshness,
            string alphabetical,
            HashSet<string> retainerAccesses,
            HashSet<string> transferHops)
        {
            CharacterSwitches =
                characterSwitches;

            SourcePriority =
                sourcePriority;

            Freshness =
                freshness;

            Alphabetical =
                alphabetical;

            RetainerAccesses =
                retainerAccesses;

            TransferHops =
                transferHops;
        }
    }
}


public sealed class GlobalTransferPlannerDiagnostics
{
    private const long MemorySampleInterval = 256;

    private readonly Stopwatch stopwatch = new();

    private long searchCalls;
    private long uniqueStates;
    private long dominatedStates;
    private long memoStates;
    private long memoEntries;
    private long generatedActions;
    private long appliedActions;
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

    internal void RecordGeneratedAction()
    {
        Interlocked.Increment(
            ref generatedActions);
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

public sealed record GlobalTransferPlannerDiagnosticsSnapshot(
    long SearchCalls,
    long UniqueStates,
    long DominatedStates,
    long MemoStates,
    long MemoEntries,
    long GeneratedActions,
    long AppliedActions,
    long MaxDepth,
    long ElapsedMilliseconds,
    long ManagedMemoryStartBytes,
    long ManagedMemoryBytes,
    long PeakManagedMemoryBytes,
    long ManagedMemoryEndBytes,
    long AllocatedBytes);
