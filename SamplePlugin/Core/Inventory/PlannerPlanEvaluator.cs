using System;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

public sealed class PlannerPlanEvaluator
{
    public PlannerPlanScore Evaluate(
        PlannerPlan plan,
        ResolutionPolicy resolutionPolicy)
    {
        var consumedStacks =
            CalculateConsumedStacks(
                plan);

        var sourcePriority =
            CalculateSourcePriority(
                plan,
                resolutionPolicy);

        var freshness =
            CalculateFreshness(
                plan);

        var alphabetical =
            CalculateAlphabeticalKey(
                plan);

        return new PlannerPlanScore(
            plan.CharacterSwitches,
            plan.RetainerAccesses,
            consumedStacks,
            plan.TransferHops,
            sourcePriority,
            freshness,
            alphabetical);
    }

    private static int CalculateConsumedStacks(
        PlannerPlan plan)
    {
        var initialStacks =
            plan.InitialState.Items
                .Where(item =>
                    item.Storage ==
                    StorageType.Retainer)
                .ToDictionary(
                    item =>
                        BuildStackKey(
                            item),
                    item =>
                        item.Quantity);

        var finalStacks =
            plan.FinalState.Items
                .Where(item =>
                    item.Storage ==
                    StorageType.Retainer)
                .ToDictionary(
                    item =>
                        BuildStackKey(
                            item),
                    item =>
                        item.Quantity);

        return initialStacks.Count(pair =>
            pair.Value > 0 &&
            !finalStacks.TryGetValue(
                pair.Key,
                out _) ||
            pair.Value > 0 &&
            finalStacks.TryGetValue(
                pair.Key,
                out var remaining) &&
            remaining == 0);
    }

    private static string BuildStackKey(
        InventoryItemSnapshot item) =>
        $"{item.OwnerId}:{item.Container}:{item.Slot}:{item.BaseItemId}:{item.IsHq}";

    private static int CalculateSourcePriority(
        PlannerPlan plan,
        ResolutionPolicy resolutionPolicy)
    {
        var logicalPolicySources =
            resolutionPolicy.Sources
                .Select(ToLogicalSourceKey)
                .Distinct()
                .ToList();

        var usedSources =
            plan.Decisions
                .Where(decision =>
                    decision.Type ==
                        PlannerDecisionType.Move &&
                    decision.Source is not null)
                .Select(decision =>
                    ToLogicalSourceKey(
                        decision.Source!))
                .Distinct();

        var total = 0;

        foreach (var source in usedSources)
        {
            var index =
                logicalPolicySources.IndexOf(
                    source);

            total +=
                index < 0
                    ? logicalPolicySources.Count
                    : index;
        }

        return total;
    }

    private static DateTime CalculateFreshness(
        PlannerPlan plan)
    {
        var timestamps =
            plan.Actions
                .Where(action =>
                    action.Type ==
                        PlannerActionType.Move &&
                    action.Source is not null)
                .SelectMany(action =>
                    plan.InitialState
                        .Find(
                            action.Source!)
                        .Where(item =>
                            item.BaseItemId ==
                                action.BaseItemId &&
                            item.IsHq ==
                                action.IsHq)
                        .Select(item =>
                            item.ObservedAtUtc))
                .ToList();

        return timestamps.Count == 0
            ? DateTime.MinValue
            : timestamps.Min();
    }

    private static string CalculateAlphabeticalKey(
        PlannerPlan plan) =>
        string.Join(
            "|",
            plan.Decisions
                .Where(decision =>
                    decision.Type ==
                        PlannerDecisionType.Move &&
                    decision.Source is not null)
                .Select(decision =>
                    decision.Source!)
                .GroupBy(ToLogicalSourceKey)
                .Select(group =>
                    BuildAlphabeticalSourceKey(
                        group.First()))
                .OrderBy(value =>
                    value,
                    StringComparer.Ordinal));

    private static string BuildAlphabeticalSourceKey(
        PlannerLogicalSource source)
    {
        var ownerName =
            string.IsNullOrWhiteSpace(
                source.OwnerName)
                ? string.Empty
                : source.OwnerName.Trim();

        return string.Join(
            ":",
            ownerName,
            source.Storage,
            source.OwnerId.ToString("D20"),
            source.ParentCharacterId.ToString("D20"));
    }

    private static LogicalSourceKey ToLogicalSourceKey(
        InventorySource source) =>
        new(
            source.Storage,
            source.OwnerId,
            source.ParentCharacterId);

    private static LogicalSourceKey ToLogicalSourceKey(
        PlannerLogicalSource source) =>
        new(
            source.Storage,
            source.OwnerId,
            source.ParentCharacterId);

    private readonly record struct LogicalSourceKey(
        StorageType Storage,
        ulong OwnerId,
        ulong ParentCharacterId);
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
        foreach (var criterion in
                 settings.Criteria)
        {
            var comparison =
                criterion switch
                {
                    OptimizationCriterion.CharacterSwitches =>
                        CharacterSwitches.CompareTo(
                            other.CharacterSwitches),

                    OptimizationCriterion.RetainerAccesses =>
                        RetainerAccesses.CompareTo(
                            other.RetainerAccesses),

                    OptimizationCriterion.ConsumedStacks =>
                        other.ConsumedStacks.CompareTo(
                            ConsumedStacks),

                    OptimizationCriterion.TransferHops =>
                        TransferHops.CompareTo(
                            other.TransferHops),

                    OptimizationCriterion.SourcePriority =>
                        SourcePriority.CompareTo(
                            other.SourcePriority),

                    OptimizationCriterion.Freshness =>
                        other.Freshness.CompareTo(
                            Freshness),

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
