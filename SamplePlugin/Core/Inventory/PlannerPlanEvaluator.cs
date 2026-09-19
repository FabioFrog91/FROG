using System;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

public sealed class PlannerPlanEvaluator
{
    public PlannerPlanScore Evaluate(
        PlannerPlan plan,
        RequirementSet requirements,
        ResolutionPolicy resolutionPolicy)
    {
        var consumedStacks = CalculateConsumedStacks(plan);
        var sourcePriority = CalculateSourcePriority(plan, resolutionPolicy);
        var freshness = CalculateFreshness(plan);
        var alphabetical = CalculateAlphabeticalKey(plan);
        var qualityFallback = CalculateQualityFallback(plan.FinalState, requirements);

        return new PlannerPlanScore(
            plan.CharacterSwitches,
            plan.RetainerAccesses,
            consumedStacks,
            plan.TransferHops,
            sourcePriority,
            freshness,
            alphabetical,
            qualityFallback);
    }

    private static int CalculateConsumedStacks(PlannerPlan plan)
    {
        var initialStacks = plan.InitialState.Items
            .Where(item => item.Storage == StorageType.Retainer)
            .ToDictionary(
                item => BuildStackKey(item),
                item => item.Quantity);

        var finalStacks = plan.FinalState.Items
            .Where(item => item.Storage == StorageType.Retainer)
            .ToDictionary(
                item => BuildStackKey(item),
                item => item.Quantity);

        return initialStacks.Count(pair =>
            pair.Value > 0 &&
            !finalStacks.TryGetValue(pair.Key, out var finalQuantity) ||
            pair.Value > 0 &&
            finalStacks.TryGetValue(pair.Key, out var remaining) &&
            remaining == 0);
    }

    private static string BuildStackKey(InventoryItemSnapshot item) =>
        $"{item.OwnerId}:{item.Container}:{item.Slot}:{item.BaseItemId}:{item.IsHq}";

    private static int CalculateSourcePriority(
        PlannerPlan plan,
        ResolutionPolicy resolutionPolicy)
    {
        var total = 0;
        var sources = resolutionPolicy.Sources.ToList();

        foreach (var action in plan.Actions)
        {
            if (action.Type != PlannerActionType.Move || action.Source is null)
                continue;

            var index = sources.IndexOf(action.Source);
            total += index < 0 ? sources.Count : index;
        }

        return total;
    }

    private static DateTime CalculateFreshness(PlannerPlan plan)
    {
        var timestamps = plan.Actions
            .Where(action =>
                action.Type == PlannerActionType.Move &&
                action.Source is not null)
            .SelectMany(action =>
                plan.InitialState
                    .Find(action.Source!)
                    .Where(item =>
                        item.BaseItemId == action.BaseItemId &&
                        item.IsHq == action.IsHq)
                    .Select(item => item.ObservedAtUtc))
            .ToList();

        return timestamps.Count == 0
            ? DateTime.MinValue
            : timestamps.Min();
    }

    private static string CalculateAlphabeticalKey(PlannerPlan plan) =>
        string.Join(
            "|",
            plan.Actions
                .Where(action =>
                    action.Type == PlannerActionType.Move &&
                    action.Source is not null)
                .Select(action =>
                    $"{action.Source!.Storage}:{action.Source.OwnerId}:{action.Source.Container}")
                .OrderBy(value => value));

    private static int CalculateQualityFallback(
        PlannerState state,
        RequirementSet requirements)
    {
        var fallback = 0;

        foreach (var requirement in requirements.Requirements)
        {
            if (requirement.QualityPolicy != RequirementQualityPolicy.HqFirst &&
                requirement.QualityPolicy != RequirementQualityPolicy.NqFirst)
            {
                continue;
            }

            var hq = state.GetMainInventoryQuantity(
                requirement.BaseItemId,
                true);

            var nq = state.GetMainInventoryQuantity(
                requirement.BaseItemId,
                false);

            if (requirement.QualityPolicy == RequirementQualityPolicy.HqFirst)
            {
                var preferred = Math.Min(requirement.Quantity, hq);
                fallback += Math.Min(
                    Math.Max(0, requirement.Quantity - preferred),
                    nq);
            }
            else
            {
                var preferred = Math.Min(requirement.Quantity, nq);
                fallback += Math.Min(
                    Math.Max(0, requirement.Quantity - preferred),
                    hq);
            }
        }

        return fallback;
    }
}

public sealed record PlannerPlanScore(
    int CharacterSwitches,
    int RetainerAccesses,
    int ConsumedStacks,
    int TransferHops,
    int SourcePriority,
    DateTime Freshness,
    string Alphabetical,
    int QualityFallback)
{
    public int CompareTo(
        PlannerPlanScore other,
        OptimizationSettings settings)
    {
        var qualityComparison =
            QualityFallback.CompareTo(other.QualityFallback);

        if (qualityComparison != 0)
            return qualityComparison;

        foreach (var criterion in settings.Criteria)
        {
            var comparison = criterion switch
            {
                OptimizationCriterion.CharacterSwitches =>
                    CharacterSwitches.CompareTo(other.CharacterSwitches),

                OptimizationCriterion.RetainerAccesses =>
                    RetainerAccesses.CompareTo(other.RetainerAccesses),

                OptimizationCriterion.ConsumedStacks =>
                    other.ConsumedStacks.CompareTo(ConsumedStacks),

                OptimizationCriterion.TransferHops =>
                    TransferHops.CompareTo(other.TransferHops),

                OptimizationCriterion.SourcePriority =>
                    SourcePriority.CompareTo(other.SourcePriority),

                OptimizationCriterion.Freshness =>
                    other.Freshness.CompareTo(Freshness),

                OptimizationCriterion.Alphabetical =>
                    string.CompareOrdinal(
                        Alphabetical,
                        other.Alphabetical),

                _ => 0
            };

            if (comparison != 0)
                return comparison;
        }

        return 0;
    }
}
