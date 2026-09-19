using FROG.Core.Inventory;
using System;
using System.Linq;

namespace SamplePlugin.Core.Inventory;

public sealed class PlannerPlanEvaluator
{
    public PlannerPlanScore Evaluate(
        PlannerPlan plan,
        ResolutionPolicy resolutionPolicy)
    {
        var retainerAccesses = plan.Actions
            .Where(action =>
                action.Type == PlannerActionType.Move &&
                action.Source is not null &&
                action.Source.Storage == StorageType.Retainer)
            .Select(action => action.Source!)
            .Distinct()
            .Count();

        var consumedStacks = CalculateConsumedStacks(plan);
        var sourcePriority = CalculateSourcePriority(plan, resolutionPolicy);
        var freshness = CalculateFreshness(plan);
        var alphabetical = CalculateAlphabeticalKey(plan);

        return new PlannerPlanScore(
            plan.CharacterSwitches,
            retainerAccesses,
            consumedStacks,
            plan.TransferHops,
            sourcePriority,
            freshness,
            alphabetical);
    }

    private static int CalculateConsumedStacks(PlannerPlan plan)
    {
        var consumed = 0;

        foreach (var action in plan.Actions)
        {
            if (action.Type != PlannerActionType.Move ||
                action.Source is null)
            {
                continue;
            }

            var before = plan.InitialState
                .Find(action.Source)
                .Where(item =>
                    item.BaseItemId == action.BaseItemId &&
                    item.IsHq == action.IsHq)
                .Sum(item => item.Quantity);

            var after = plan.FinalState
                .Find(action.Source)
                .Where(item =>
                    item.BaseItemId == action.BaseItemId &&
                    item.IsHq == action.IsHq)
                .Sum(item => item.Quantity);

            if (before > 0 && after == 0)
                consumed++;
        }

        return consumed;
    }

    private static int CalculateSourcePriority(
        PlannerPlan plan,
        ResolutionPolicy resolutionPolicy)
    {
        var total = 0;

        foreach (var action in plan.Actions)
        {
            if (action.Type != PlannerActionType.Move ||
                action.Source is null)
            {
                continue;
            }

            var index = resolutionPolicy.Sources
                .ToList()
                .IndexOf(action.Source);

            total += index < 0
                ? resolutionPolicy.Sources.Count
                : index;
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
            : timestamps.Max();
    }

    private static string CalculateAlphabeticalKey(PlannerPlan plan)
    {
        return string.Join(
            "|",
            plan.Actions
                .Where(action =>
                    action.Type == PlannerActionType.Move &&
                    action.Source is not null)
                .Select(action =>
                    $"{action.Source!.Storage}:{action.Source.OwnerId}:{action.Source.Container}")
                .OrderBy(value => value));
    }
}

public sealed record PlannerPlanScore(
    int CharacterSwitches,
    int RetainerAccesses,
    int ConsumedStacks,
    int TransferHops,
    int SourcePriority,
    DateTime Freshness,
    string Alphabetical)
{
    public int CompareTo(
        PlannerPlanScore other,
        OptimizationSettings settings)
    {
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
