using System;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

public sealed class OptimizationSettings
{
    private readonly List<OptimizationCriterion> criteria;

    public IReadOnlyList<OptimizationCriterion> Criteria =>
        criteria;

    public OptimizationSettings()
    {
        criteria = new List<OptimizationCriterion>
        {
            OptimizationCriterion.CharacterSwitches,
            OptimizationCriterion.RetainerAccesses,
            OptimizationCriterion.ConsumedStacks,
            OptimizationCriterion.TransferHops,
            OptimizationCriterion.SourcePriority,
            OptimizationCriterion.Freshness,
            OptimizationCriterion.Alphabetical
        };
    }

    public OptimizationSettings(
        IEnumerable<OptimizationCriterion> criteria)
    {
        this.criteria = criteria.ToList();

        EnsureAllCriteriaArePresent();
    }

    public void Move(
        OptimizationCriterion criterion,
        int targetIndex)
    {
        if (!criteria.Contains(criterion))
            throw new ArgumentException(
                "The optimization criterion is not configured.",
                nameof(criterion));

        if (targetIndex < 0 ||
            targetIndex >= criteria.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetIndex));
        }

        var currentIndex =
            criteria.IndexOf(criterion);

        if (currentIndex == targetIndex)
            return;

        criteria.RemoveAt(currentIndex);
        criteria.Insert(targetIndex, criterion);
    }

    public bool Contains(
        OptimizationCriterion criterion)
    {
        return criteria.Contains(criterion);
    }

    private void EnsureAllCriteriaArePresent()
    {
        foreach (var criterion in Enum.GetValues<OptimizationCriterion>())
        {
            if (!criteria.Contains(criterion))
            {
                throw new InvalidOperationException(
                    $"Optimization criterion '{criterion}' is missing.");
            }
        }

        if (criteria.Count !=
            Enum.GetValues<OptimizationCriterion>().Length)
        {
            throw new InvalidOperationException(
                "Optimization criteria must contain each criterion exactly once.");
        }
    }
}
