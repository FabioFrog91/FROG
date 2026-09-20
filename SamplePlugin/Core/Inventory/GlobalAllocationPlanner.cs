using System;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

/// <summary>
/// Optimizes which physical inventory stacks must contribute to the requested
/// result. It deliberately does not search permutations of concrete MOVE and
/// SWITCH actions. Once the useful sources are selected, the physical action
/// sequence is compiled deterministically.
/// </summary>
internal sealed class GlobalAllocationPlanner
{
    private readonly PlannerActionValidator actionValidator;
    private readonly PlannerPlanEvaluator planEvaluator;
    private readonly GlobalTransferPlannerDiagnostics diagnostics;

    public GlobalAllocationPlanner(
        PlannerActionValidator actionValidator,
        PlannerPlanEvaluator planEvaluator,
        GlobalTransferPlannerDiagnostics diagnostics)
    {
        this.actionValidator = actionValidator;
        this.planEvaluator = planEvaluator;
        this.diagnostics = diagnostics;
    }

    public PlannerPlan Plan(
        RequirementSet requirements,
        PlannerState initialState,
        ResolutionPolicy resolutionPolicy,
        OptimizationSettings optimizationSettings)
    {
        diagnostics.Reset();
        diagnostics.Start();

        var fixedAllocations =
            new List<AllocationPick>();

        var reservedFreeCompany =
            new List<AllocationPick>();

        var choiceRequirements =
            new List<RequirementChoices>();

        var unavoidableMissing = 0;

        try
        {
            foreach (var requirement in requirements.Requirements)
            {
                var analysis =
                    AnalyzeRequirement(
                        requirement,
                        initialState,
                        resolutionPolicy);

                unavoidableMissing +=
                    analysis.Missing;

                reservedFreeCompany.AddRange(
                    analysis.ReservedFreeCompany);

                if (analysis.Options.Count == 0)
                    continue;

                if (analysis.Options.Count == 1)
                {
                    fixedAllocations.AddRange(
                        analysis.Options[0]);

                    continue;
                }

                choiceRequirements.Add(
                    new RequirementChoices(
                        requirement,
                        analysis.Options));
            }

            choiceRequirements =
                choiceRequirements
                    .OrderBy(choice =>
                        choice.Options.Count)
                    .ThenBy(choice =>
                        choice.Requirement.BaseItemId)
                    .ToList();

            PlannerPlan? bestPlan = null;
            PlannerPlanScore? bestScore = null;

            SearchChoices(
                requirements,
                initialState,
                resolutionPolicy,
                optimizationSettings,
                fixedAllocations,
                reservedFreeCompany,
                choiceRequirements,
                0,
                new List<AllocationPick>(),
                unavoidableMissing,
                ref bestPlan,
                ref bestScore);

            if (bestPlan is not null)
                return bestPlan;

            // A defensive fallback: this should only be reached if every
            // allocation combination failed physical validation.
            var fallback =
                CompilePlan(
                    requirements,
                    initialState,
                    resolutionPolicy,
                    fixedAllocations,
                    reservedFreeCompany,
                    unavoidableMissing);

            if (fallback is not null)
                return fallback;

            var missing =
                CalculateMissing(
                    requirements,
                    initialState);

            return new PlannerPlan(
                initialState,
                initialState,
                Array.Empty<PlannerAction>(),
                missing == 0
                    ? PlannerPlanResult.Completed
                    : PlannerPlanResult.CompletedWithMissing,
                missing);
        }
        finally
        {
            var snapshot =
                diagnostics.Snapshot();

            diagnostics.Complete(
                checked((int)Math.Min(
                    int.MaxValue,
                    snapshot.UniqueStates)),
                checked((int)Math.Min(
                    int.MaxValue,
                    snapshot.MemoEntries)));
        }
    }

    private void SearchChoices(
        RequirementSet requirements,
        PlannerState initialState,
        ResolutionPolicy resolutionPolicy,
        OptimizationSettings optimizationSettings,
        IReadOnlyList<AllocationPick> fixedAllocations,
        IReadOnlyList<AllocationPick> reservedFreeCompany,
        IReadOnlyList<RequirementChoices> choices,
        int index,
        List<AllocationPick> selected,
        int unavoidableMissing,
        ref PlannerPlan? bestPlan,
        ref PlannerPlanScore? bestScore)
    {
        diagnostics.RecordSearch(
            index);

        if (index >= choices.Count)
        {
            diagnostics.RecordMemoRegistration(
                newMemoState: true,
                memoEntryDelta: 1);

            var allAllocations =
                new List<AllocationPick>(
                    fixedAllocations.Count +
                    selected.Count);

            allAllocations.AddRange(
                fixedAllocations);

            allAllocations.AddRange(
                selected);

            var candidate =
                CompilePlan(
                    requirements,
                    initialState,
                    resolutionPolicy,
                    allAllocations,
                    reservedFreeCompany,
                    unavoidableMissing);

            if (candidate is null)
            {
                diagnostics.RecordDominatedState();
                return;
            }

            var score =
                planEvaluator.Evaluate(
                    candidate,
                    requirements,
                    resolutionPolicy);

            if (bestPlan is null ||
                candidate.Missing < bestPlan.Missing ||
                candidate.Missing == bestPlan.Missing &&
                score.CompareTo(
                    bestScore!,
                    optimizationSettings) < 0)
            {
                bestPlan = candidate;
                bestScore = score;
            }
            else
            {
                diagnostics.RecordDominatedState();
            }

            return;
        }

        var choice =
            choices[index];

        foreach (var option in choice.Options)
        {
            selected.AddRange(
                option);

            SearchChoices(
                requirements,
                initialState,
                resolutionPolicy,
                optimizationSettings,
                fixedAllocations,
                reservedFreeCompany,
                choices,
                index + 1,
                selected,
                unavoidableMissing,
                ref bestPlan,
                ref bestScore);

            selected.RemoveRange(
                selected.Count - option.Count,
                option.Count);
        }
    }

    private RequirementAnalysis AnalyzeRequirement(
        Requirement requirement,
        PlannerState state,
        ResolutionPolicy resolutionPolicy)
    {
        var relevantItems =
            state.Items
                .Where(item =>
                    item.BaseItemId == requirement.BaseItemId)
                .ToList();

        var mainItems =
            relevantItems
                .Where(item =>
                    item.Storage == StorageType.CharacterInventory &&
                    item.OwnerId == state.MainCharacterId)
                .ToList();

        var freeCompanyItems =
            relevantItems
                .Where(item =>
                    item.Storage == StorageType.FreeCompanyChest)
                .ToList();

        var externalItems =
            relevantItems
                .Where(item =>
                    !(item.Storage == StorageType.CharacterInventory &&
                      item.OwnerId == state.MainCharacterId) &&
                    item.Storage != StorageType.FreeCompanyChest)
                .Where(item =>
                    FindPolicySource(
                        resolutionPolicy,
                        item) is not null)
                .ToList();

        var mainHq =
            SumQuality(
                mainItems,
                true);

        var mainNq =
            SumQuality(
                mainItems,
                false);

        var fcHq =
            SumQuality(
                freeCompanyItems,
                true);

        var fcNq =
            SumQuality(
                freeCompanyItems,
                false);

        var externalHq =
            SumQuality(
                externalItems,
                true);

        var externalNq =
            SumQuality(
                externalItems,
                false);

        var desired =
            CalculateDesiredQuality(
                requirement,
                mainHq + fcHq + externalHq,
                mainNq + fcNq + externalNq);

        var missing =
            Math.Max(
                0,
                requirement.Quantity -
                desired.Total);

        var reservedFreeCompany =
            new List<AllocationPick>();

        var targets =
            new List<ExternalTarget>();

        if (requirement.QualityPolicy == RequirementQualityPolicy.Any)
        {
            var desiredTotal =
                desired.Total;

            var mainUsed =
                Math.Min(
                    desiredTotal,
                    mainHq + mainNq);

            var afterMain =
                desiredTotal - mainUsed;

            var fcUsed =
                Math.Min(
                    afterMain,
                    fcHq + fcNq);

            ReserveAnyFreeCompany(
                freeCompanyItems,
                fcUsed,
                resolutionPolicy,
                reservedFreeCompany);

            var externalTarget =
                afterMain - fcUsed;

            if (externalTarget > 0)
            {
                targets.Add(
                    new ExternalTarget(
                        externalTarget,
                        null));
            }
        }
        else
        {
            AddQualityTarget(
                desired.Hq,
                true,
                mainHq,
                freeCompanyItems,
                externalItems,
                resolutionPolicy,
                reservedFreeCompany,
                targets);

            AddQualityTarget(
                desired.Nq,
                false,
                mainNq,
                freeCompanyItems,
                externalItems,
                resolutionPolicy,
                reservedFreeCompany,
                targets);
        }

        var optionSets =
            new List<IReadOnlyList<IReadOnlyList<AllocationPick>>>();

        foreach (var target in targets)
        {
            var candidates =
                externalItems
                    .Where(item =>
                        target.IsHq is null ||
                        item.IsHq == target.IsHq.Value)
                    .OrderBy(item =>
                        GetSourcePriority(
                            resolutionPolicy,
                            item))
                    .ThenBy(item =>
                        item.OwnerId)
                    .ThenBy(item =>
                        item.Container)
                    .ThenBy(item =>
                        item.Slot)
                    .ThenBy(item =>
                        item.IsHq)
                    .ToList();

            var options =
                BuildAllocationOptions(
                    candidates,
                    target.Quantity,
                    resolutionPolicy);

            if (options.Count == 0 &&
                target.Quantity > 0)
            {
                // The availability calculation should make this impossible,
                // but keep the planner best-effort rather than throwing.
                continue;
            }

            optionSets.Add(
                options);
        }

        var combinedOptions =
            CombineOptions(
                optionSets);

        return new RequirementAnalysis(
            missing,
            reservedFreeCompany,
            combinedOptions);
    }

    private static DesiredQuality CalculateDesiredQuality(
        Requirement requirement,
        int totalHq,
        int totalNq)
    {
        return requirement.QualityPolicy switch
        {
            RequirementQualityPolicy.HqOnly =>
                new DesiredQuality(
                    Math.Min(
                        requirement.Quantity,
                        totalHq),
                    0),

            RequirementQualityPolicy.NqOnly =>
                new DesiredQuality(
                    0,
                    Math.Min(
                        requirement.Quantity,
                        totalNq)),

            RequirementQualityPolicy.HqFirst =>
                CalculatePreferredQuality(
                    requirement.Quantity,
                    totalHq,
                    totalNq,
                    preferredIsHq: true),

            RequirementQualityPolicy.NqFirst =>
                CalculatePreferredQuality(
                    requirement.Quantity,
                    totalHq,
                    totalNq,
                    preferredIsHq: false),

            RequirementQualityPolicy.Any =>
                new DesiredQuality(
                    Math.Min(
                        requirement.Quantity,
                        totalHq + totalNq),
                    0,
                    IsAny: true),

            _ =>
                new DesiredQuality(
                    0,
                    0)
        };
    }

    private static DesiredQuality CalculatePreferredQuality(
        int required,
        int totalHq,
        int totalNq,
        bool preferredIsHq)
    {
        var preferredAvailable =
            preferredIsHq
                ? totalHq
                : totalNq;

        var fallbackAvailable =
            preferredIsHq
                ? totalNq
                : totalHq;

        var preferred =
            Math.Min(
                required,
                preferredAvailable);

        var fallback =
            Math.Min(
                required - preferred,
                fallbackAvailable);

        return preferredIsHq
            ? new DesiredQuality(
                preferred,
                fallback)
            : new DesiredQuality(
                fallback,
                preferred);
    }

    private static void AddQualityTarget(
        int desiredQuantity,
        bool isHq,
        int mainQuantity,
        IReadOnlyList<InventoryItemSnapshot> freeCompanyItems,
        IReadOnlyList<InventoryItemSnapshot> externalItems,
        ResolutionPolicy resolutionPolicy,
        List<AllocationPick> reservedFreeCompany,
        List<ExternalTarget> targets)
    {
        if (desiredQuantity <= 0)
            return;

        var mainUsed =
            Math.Min(
                desiredQuantity,
                mainQuantity);

        var afterMain =
            desiredQuantity - mainUsed;

        if (afterMain <= 0)
            return;

        var fcAvailable =
            freeCompanyItems
                .Where(item =>
                    item.IsHq == isHq)
                .Sum(item =>
                    item.Quantity);

        var fcUsed =
            Math.Min(
                afterMain,
                fcAvailable);

        ReserveFreeCompanyQuality(
            freeCompanyItems,
            isHq,
            fcUsed,
            resolutionPolicy,
            reservedFreeCompany);

        var externalTarget =
            afterMain - fcUsed;

        if (externalTarget <= 0)
            return;

        var externalAvailable =
            externalItems
                .Where(item =>
                    item.IsHq == isHq)
                .Sum(item =>
                    item.Quantity);

        targets.Add(
            new ExternalTarget(
                Math.Min(
                    externalTarget,
                    externalAvailable),
                isHq));
    }

    private static void ReserveFreeCompanyQuality(
        IReadOnlyList<InventoryItemSnapshot> freeCompanyItems,
        bool isHq,
        int quantity,
        ResolutionPolicy resolutionPolicy,
        List<AllocationPick> result)
    {
        if (quantity <= 0)
            return;

        ReserveFromItems(
            freeCompanyItems
                .Where(item =>
                    item.IsHq == isHq),
            quantity,
            resolutionPolicy,
            result);
    }

    private static void ReserveAnyFreeCompany(
        IReadOnlyList<InventoryItemSnapshot> freeCompanyItems,
        int quantity,
        ResolutionPolicy resolutionPolicy,
        List<AllocationPick> result)
    {
        if (quantity <= 0)
            return;

        // Quality has no semantic preference for Any. NQ first is simply a
        // stable tie-break that avoids branching on an irrelevant distinction.
        ReserveFromItems(
            freeCompanyItems
                .OrderBy(item =>
                    item.IsHq)
                .ThenBy(item =>
                    item.Container)
                .ThenBy(item =>
                    item.Slot),
            quantity,
            resolutionPolicy,
            result);
    }

    private static void ReserveFromItems(
        IEnumerable<InventoryItemSnapshot> items,
        int quantity,
        ResolutionPolicy resolutionPolicy,
        List<AllocationPick> result)
    {
        var remaining =
            quantity;

        foreach (var item in items
                     .OrderBy(item =>
                         item.Container)
                     .ThenBy(item =>
                         item.Slot)
                     .ThenBy(item =>
                         item.BaseItemId))
        {
            if (remaining <= 0)
                break;

            var used =
                Math.Min(
                    remaining,
                    item.Quantity);

            if (used <= 0)
                continue;

            var source =
                FindPolicySource(
                    resolutionPolicy,
                    item);

            if (source is null)
                continue;

            result.Add(
                new AllocationPick(
                    item,
                    source,
                    used));

            remaining -=
                used;
        }
    }

    private static IReadOnlyList<IReadOnlyList<AllocationPick>> BuildAllocationOptions(
        IReadOnlyList<InventoryItemSnapshot> candidates,
        int quantity,
        ResolutionPolicy resolutionPolicy)
    {
        if (quantity <= 0)
        {
            return new[]
            {
                (IReadOnlyList<AllocationPick>)Array.Empty<AllocationPick>()
            };
        }

        // The physical PlannerAction addresses a source/container, not a
        // particular slot. Therefore multiple stacks of the same item/quality
        // inside one source are one optimization capacity. PlannerState.Move
        // consumes the real stacks in slot order later.
        var units =
            candidates
                .GroupBy(item =>
                    new
                    {
                        item.Storage,
                        item.OwnerId,
                        item.Container,
                        item.IsHq
                    })
                .Select(group =>
                {
                    var firstItem =
                        group
                            .OrderBy(item =>
                                item.Slot)
                            .First();

                    return new AllocationUnit(
                        firstItem,
                        FindPolicySource(
                            resolutionPolicy,
                            firstItem)!,
                        group.Sum(item =>
                            item.Quantity));
                })
                .Where(unit =>
                    unit.Source is not null)
                .OrderBy(unit =>
                    GetSourcePriority(
                        resolutionPolicy,
                        unit.Item))
                .ThenBy(unit =>
                    unit.Source.OwnerId)
                .ThenBy(unit =>
                    unit.Source.Container)
                .ThenBy(unit =>
                    unit.Item.Slot)
                .ThenBy(unit =>
                    unit.Item.IsHq)
                .ToList();

        var total =
            units.Sum(unit =>
                unit.Quantity);

        if (total <= quantity)
        {
            var forced =
                units
                    .Select(unit =>
                        new AllocationPick(
                            unit.Item,
                            unit.Source,
                            unit.Quantity))
                    .ToList();

            return new[]
            {
                (IReadOnlyList<AllocationPick>)forced
            };
        }

        var suffix =
            new int[units.Count + 1];

        for (var i = units.Count - 1;
             i >= 0;
             i--)
        {
            suffix[i] =
                suffix[i + 1] +
                units[i].Quantity;
        }

        var results =
            new List<IReadOnlyList<AllocationPick>>();

        EnumerateAllocationOptions(
            units,
            suffix,
            0,
            quantity,
            new List<AllocationPick>(),
            results);

        return DeduplicateOptions(
            results);
    }

    private static void EnumerateAllocationOptions(
        IReadOnlyList<AllocationUnit> units,
        IReadOnlyList<int> suffix,
        int index,
        int remaining,
        List<AllocationPick> current,
        List<IReadOnlyList<AllocationPick>> results)
    {
        if (remaining == 0)
        {
            results.Add(
                current.ToList());

            return;
        }

        if (index >= units.Count ||
            suffix[index] < remaining)
        {
            return;
        }

        var unit =
            units[index];

        EnumerateAllocationOptions(
            units,
            suffix,
            index + 1,
            remaining,
            current,
            results);

        var used =
            Math.Min(
                remaining,
                unit.Quantity);

        current.Add(
            new AllocationPick(
                unit.Item,
                unit.Source,
                used));

        EnumerateAllocationOptions(
            units,
            suffix,
            index + 1,
            remaining - used,
            current,
            results);

        current.RemoveAt(
            current.Count - 1);
    }

    private static IReadOnlyList<IReadOnlyList<AllocationPick>> DeduplicateOptions(
        IEnumerable<IReadOnlyList<AllocationPick>> options)
    {
        var seen =
            new HashSet<string>(
                StringComparer.Ordinal);

        var result =
            new List<IReadOnlyList<AllocationPick>>();

        foreach (var option in options)
        {
            var key =
                string.Join(
                    ";",
                    option
                        .OrderBy(pick =>
                            pick.Source.Storage)
                        .ThenBy(pick =>
                            pick.Source.OwnerId)
                        .ThenBy(pick =>
                            pick.Source.Container)
                        .ThenBy(pick =>
                            pick.Item.IsHq)
                        .Select(pick =>
                            $"{pick.Source.Storage}:{pick.Source.OwnerId}:{pick.Source.Container}:" +
                            $"{pick.Item.IsHq}:{pick.Quantity}"));

            if (seen.Add(key))
                result.Add(option);
        }

        return result;
    }

    private static IReadOnlyList<IReadOnlyList<AllocationPick>> CombineOptions(
        IReadOnlyList<IReadOnlyList<IReadOnlyList<AllocationPick>>> optionSets)
    {
        if (optionSets.Count == 0)
        {
            return new[]
            {
                (IReadOnlyList<AllocationPick>)Array.Empty<AllocationPick>()
            };
        }

        var combined =
            new List<IReadOnlyList<AllocationPick>>();

        CombineOptionsRecursive(
            optionSets,
            0,
            new List<AllocationPick>(),
            combined);

        return DeduplicateOptions(
            combined);
    }

    private static void CombineOptionsRecursive(
        IReadOnlyList<IReadOnlyList<IReadOnlyList<AllocationPick>>> optionSets,
        int index,
        List<AllocationPick> current,
        List<IReadOnlyList<AllocationPick>> result)
    {
        if (index >= optionSets.Count)
        {
            result.Add(
                current.ToList());

            return;
        }

        foreach (var option in optionSets[index])
        {
            current.AddRange(
                option);

            CombineOptionsRecursive(
                optionSets,
                index + 1,
                current,
                result);

            current.RemoveRange(
                current.Count - option.Count,
                option.Count);
        }
    }

    private PlannerPlan? CompilePlan(
        RequirementSet requirements,
        PlannerState initialState,
        ResolutionPolicy resolutionPolicy,
        IReadOnlyList<AllocationPick> allocations,
        IReadOnlyList<AllocationPick> reservedFreeCompany,
        int expectedMissing)
    {
        var actions =
            new List<PlannerAction>();

        var state =
            initialState;

        var currentCharacterId =
            state.MainCharacterId;

        var mainRetainerAllocations =
            allocations
                .Where(pick =>
                    pick.Source.Storage == StorageType.Retainer &&
                    pick.Source.ParentCharacterId == state.MainCharacterId)
                .ToList();

        var mainCharacterInventory =
            FindCharacterInventory(
                resolutionPolicy,
                state.MainCharacterId);

        if (mainCharacterInventory is null)
            return null;

        if (!AppendGroupedMoves(
                ref state,
                actions,
                mainRetainerAllocations,
                _ =>
                    mainCharacterInventory))
        {
            return null;
        }

        var alternateCharacterIds =
            allocations
                .Select(pick =>
                    GetCharacterId(
                        pick.Source))
                .Where(characterId =>
                    characterId != 0 &&
                    characterId != state.MainCharacterId)
                .Distinct()
                .OrderBy(characterId =>
                    GetCharacterPriority(
                        allocations,
                        resolutionPolicy,
                        characterId))
                .ThenBy(characterId =>
                    characterId)
                .ToList();

        var bridgedToFreeCompany =
            new List<BridgedQuantity>();

        foreach (var characterId in alternateCharacterIds)
        {
            if (!AppendSwitch(
                    ref state,
                    actions,
                    currentCharacterId,
                    characterId))
            {
                return null;
            }

            currentCharacterId =
                characterId;

            var characterInventory =
                FindCharacterInventory(
                    resolutionPolicy,
                    characterId);

            var freeCompany =
                FindFreeCompany(
                    resolutionPolicy);

            if (characterInventory is null ||
                freeCompany is null)
            {
                return null;
            }

            var retainerAllocations =
                allocations
                    .Where(pick =>
                        pick.Source.Storage == StorageType.Retainer &&
                        pick.Source.ParentCharacterId == characterId)
                    .ToList();

            if (!AppendGroupedMoves(
                    ref state,
                    actions,
                    retainerAllocations,
                    _ =>
                        characterInventory))
            {
                return null;
            }

            foreach (var group in retainerAllocations
                         .GroupBy(pick =>
                             new
                             {
                                 pick.Item.BaseItemId,
                                 pick.Item.IsHq
                             }))
            {
                bridgedToFreeCompany.Add(
                    new BridgedQuantity(
                        group.Key.BaseItemId,
                        group.Key.IsHq,
                        group.Sum(pick =>
                            pick.Quantity)));
            }

            var inventoryAllocations =
                allocations
                    .Where(pick =>
                        pick.Source.Storage == StorageType.CharacterInventory &&
                        pick.Source.OwnerId == characterId)
                    .ToList();

            var inventoryOne =
                inventoryAllocations
                    .Where(pick =>
                        SameSource(
                            pick.Source,
                            characterInventory))
                    .ToList();

            foreach (var group in inventoryOne
                         .GroupBy(pick =>
                             new
                             {
                                 pick.Item.BaseItemId,
                                 pick.Item.IsHq
                             }))
            {
                bridgedToFreeCompany.Add(
                    new BridgedQuantity(
                        group.Key.BaseItemId,
                        group.Key.IsHq,
                        group.Sum(pick =>
                            pick.Quantity)));
            }

            var inventoryOneBridge =
                retainerAllocations
                    .Concat(
                        inventoryOne)
                    .GroupBy(pick =>
                        new
                        {
                            pick.Item.BaseItemId,
                            pick.Item.IsHq
                        })
                    .Select(group =>
                        new GroupedMove(
                            characterInventory,
                            freeCompany,
                            group.Key.BaseItemId,
                            group.Key.IsHq,
                            group.Sum(pick =>
                                pick.Quantity),
                            int.MaxValue,
                            int.MaxValue))
                    .ToList();

            if (!AppendMoves(
                    ref state,
                    actions,
                    inventoryOneBridge))
            {
                return null;
            }

            var otherInventory =
                inventoryAllocations
                    .Where(pick =>
                        !SameSource(
                            pick.Source,
                            characterInventory))
                    .ToList();

            if (!AppendGroupedMoves(
                    ref state,
                    actions,
                    otherInventory,
                    _ =>
                        freeCompany))
            {
                return null;
            }

            foreach (var group in otherInventory
                         .GroupBy(pick =>
                             new
                             {
                                 pick.Item.BaseItemId,
                                 pick.Item.IsHq
                             }))
            {
                bridgedToFreeCompany.Add(
                    new BridgedQuantity(
                        group.Key.BaseItemId,
                        group.Key.IsHq,
                        group.Sum(pick =>
                            pick.Quantity)));
            }
        }

        if (currentCharacterId != state.MainCharacterId)
        {
            if (!AppendSwitch(
                    ref state,
                    actions,
                    currentCharacterId,
                    state.MainCharacterId))
            {
                return null;
            }

            currentCharacterId =
                state.MainCharacterId;
        }

        var freeCompanyMoves =
            BuildFreeCompanyToMainMoves(
                resolutionPolicy,
                reservedFreeCompany,
                bridgedToFreeCompany,
                mainCharacterInventory);

        if (!AppendMoves(
                ref state,
                actions,
                freeCompanyMoves))
        {
            return null;
        }

        var missing =
            CalculateMissing(
                requirements,
                state);

        // expectedMissing is calculated before search and should agree with
        // the fully materialized state. Keep the state-derived value as the
        // source of truth, but reject an allocation that unexpectedly loses
        // satisfiable material.
        if (missing > expectedMissing)
            return null;

        foreach (var action in actions)
        {
            diagnostics.RecordGeneratedAction(
                action);

            diagnostics.RecordAppliedAction();
        }

        return new PlannerPlan(
            initialState,
            state,
            actions,
            missing == 0
                ? PlannerPlanResult.Completed
                : PlannerPlanResult.CompletedWithMissing,
            missing);
    }

    private bool AppendGroupedMoves(
        ref PlannerState state,
        List<PlannerAction> actions,
        IReadOnlyList<AllocationPick> picks,
        Func<InventorySource, InventorySource?> destinationSelector)
    {
        var moves =
            picks
                .GroupBy(pick =>
                    new
                    {
                        pick.Source,
                        pick.Item.BaseItemId,
                        pick.Item.IsHq
                    })
                .Select(group =>
                {
                    var source =
                        group.Key.Source;

                    return new GroupedMove(
                        source,
                        destinationSelector(
                            source),
                        group.Key.BaseItemId,
                        group.Key.IsHq,
                        group.Sum(pick =>
                            pick.Quantity),
                        source.Container,
                        group.Min(pick =>
                            pick.Item.Slot));
                })
                .Where(move =>
                    move.Destination is not null)
                .Select(move =>
                    move with
                    {
                        Destination =
                            move.Destination!
                    })
                .ToList();

        return AppendMoves(
            ref state,
            actions,
            moves);
    }

    private bool AppendMoves(
        ref PlannerState state,
        List<PlannerAction> actions,
        IEnumerable<GroupedMove> moves)
    {
        foreach (var move in moves
                     .OrderBy(move =>
                         move.Source.Storage)
                     .ThenBy(move =>
                         move.Source.OwnerId)
                     .ThenBy(move =>
                         move.ContainerOrder)
                     .ThenBy(move =>
                         move.SlotOrder)
                     .ThenBy(move =>
                         move.BaseItemId)
                     .ThenBy(move =>
                         move.IsHq))
        {
            if (move.Destination is null)
                return false;

            var action =
                PlannerAction.Move(
                    move.Source,
                    move.Destination,
                    move.BaseItemId,
                    move.IsHq,
                    move.Quantity);

            if (!AppendAction(
                    ref state,
                    actions,
                    action))
            {
                return false;
            }
        }

        return true;
    }

    private bool AppendSwitch(
        ref PlannerState state,
        List<PlannerAction> actions,
        ulong fromCharacterId,
        ulong toCharacterId)
    {
        var action =
            PlannerAction.SwitchCharacter(
                fromCharacterId,
                toCharacterId);

        return AppendAction(
            ref state,
            actions,
            action);
    }

    private bool AppendAction(
        ref PlannerState state,
        List<PlannerAction> actions,
        PlannerAction action)
    {
        if (!actionValidator.CanApply(
                state,
                action,
                out _))
        {
            return false;
        }

        try
        {
            state =
                action.Type switch
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
                        state
                };
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        actions.Add(
            action);

        return true;
    }

    private static IReadOnlyList<GroupedMove> BuildFreeCompanyToMainMoves(
        ResolutionPolicy resolutionPolicy,
        IReadOnlyList<AllocationPick> reservedFreeCompany,
        IReadOnlyList<BridgedQuantity> bridged,
        InventorySource mainInventory)
    {
        var moves =
            new List<GroupedMove>();

        foreach (var group in reservedFreeCompany
                     .GroupBy(pick =>
                         new
                         {
                             pick.Source,
                             pick.Item.BaseItemId,
                             pick.Item.IsHq
                         }))
        {
            moves.Add(
                new GroupedMove(
                    group.Key.Source,
                    mainInventory,
                    group.Key.BaseItemId,
                    group.Key.IsHq,
                    group.Sum(pick =>
                        pick.Quantity),
                    group.Key.Source.Container,
                    group.Min(pick =>
                        pick.Item.Slot)));
        }

        var bridgeSource =
            FindFreeCompany(
                resolutionPolicy);

        if (bridgeSource is not null)
        {
            foreach (var group in bridged
                         .GroupBy(value =>
                             new
                             {
                                 value.BaseItemId,
                                 value.IsHq
                             }))
            {
                var existing =
                    moves.FindIndex(move =>
                        SameSource(
                            move.Source,
                            bridgeSource) &&
                        move.BaseItemId == group.Key.BaseItemId &&
                        move.IsHq == group.Key.IsHq);

                var quantity =
                    group.Sum(value =>
                        value.Quantity);

                if (existing >= 0)
                {
                    var current =
                        moves[existing];

                    moves[existing] =
                        current with
                        {
                            Quantity =
                                current.Quantity +
                                quantity
                        };
                }
                else
                {
                    moves.Add(
                        new GroupedMove(
                            bridgeSource,
                            mainInventory,
                            group.Key.BaseItemId,
                            group.Key.IsHq,
                            quantity,
                            bridgeSource.Container,
                            int.MaxValue));
                }
            }
        }

        return moves;
    }

    private static InventorySource? FindPolicySource(
        ResolutionPolicy resolutionPolicy,
        InventoryItemSnapshot item) =>
        resolutionPolicy.Sources
            .FirstOrDefault(source =>
                source.Storage == item.Storage &&
                source.OwnerId == item.OwnerId &&
                source.Container == item.Container);

    private static InventorySource? FindCharacterInventory(
        ResolutionPolicy resolutionPolicy,
        ulong characterId) =>
        resolutionPolicy.Sources
            .Where(source =>
                source.Storage == StorageType.CharacterInventory &&
                source.OwnerId == characterId)
            .OrderBy(source =>
                source.Container)
            .FirstOrDefault();

    private static InventorySource? FindFreeCompany(
        ResolutionPolicy resolutionPolicy) =>
        resolutionPolicy.Sources
            .Where(source =>
                source.Storage == StorageType.FreeCompanyChest)
            .OrderBy(source =>
                source.Container)
            .FirstOrDefault();

    private static int GetSourcePriority(
        ResolutionPolicy resolutionPolicy,
        InventoryItemSnapshot item)
    {
        for (var i = 0;
             i < resolutionPolicy.Sources.Count;
             i++)
        {
            var source =
                resolutionPolicy.Sources[i];

            if (source.Storage == item.Storage &&
                source.OwnerId == item.OwnerId &&
                source.Container == item.Container)
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private static int GetCharacterPriority(
        IReadOnlyList<AllocationPick> allocations,
        ResolutionPolicy resolutionPolicy,
        ulong characterId) =>
        allocations
            .Where(pick =>
                GetCharacterId(
                    pick.Source) == characterId)
            .Select(pick =>
                GetSourcePriority(
                    resolutionPolicy,
                    pick.Item))
            .DefaultIfEmpty(
                int.MaxValue)
            .Min();

    private static ulong GetCharacterId(
        InventorySource source) =>
        source.Storage switch
        {
            StorageType.CharacterInventory =>
                source.OwnerId,

            StorageType.Retainer =>
                source.ParentCharacterId,

            _ =>
                0
        };

    private static int SumQuality(
        IEnumerable<InventoryItemSnapshot> items,
        bool isHq) =>
        items
            .Where(item =>
                item.IsHq == isHq)
            .Sum(item =>
                item.Quantity);

    private static bool SameSource(
        InventorySource left,
        InventorySource right) =>
        left.Storage == right.Storage &&
        left.OwnerId == right.OwnerId &&
        left.Container == right.Container;

    private static int CalculateMissing(
        RequirementSet requirements,
        PlannerState state) =>
        requirements.Requirements.Sum(requirement =>
            Math.Max(
                0,
                requirement.Quantity -
                GetSatisfiedMainQuantity(
                    requirement,
                    state)));

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

    private sealed record RequirementChoices(
        Requirement Requirement,
        IReadOnlyList<IReadOnlyList<AllocationPick>> Options);

    private sealed record RequirementAnalysis(
        int Missing,
        IReadOnlyList<AllocationPick> ReservedFreeCompany,
        IReadOnlyList<IReadOnlyList<AllocationPick>> Options);

    private sealed record AllocationPick(
        InventoryItemSnapshot Item,
        InventorySource Source,
        int Quantity);

    private sealed record AllocationUnit(
        InventoryItemSnapshot Item,
        InventorySource Source,
        int Quantity);

    private sealed record ExternalTarget(
        int Quantity,
        bool? IsHq);

    private sealed record DesiredQuality(
        int Hq,
        int Nq,
        bool IsAny = false)
    {
        public int Total =>
            IsAny
                ? Hq
                : Hq + Nq;
    }

    private sealed record BridgedQuantity(
        uint BaseItemId,
        bool IsHq,
        int Quantity);

    private sealed record GroupedMove(
        InventorySource Source,
        InventorySource? Destination,
        uint BaseItemId,
        bool IsHq,
        int Quantity,
        int ContainerOrder,
        int SlotOrder);
}
