using System;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

public sealed class OptimizationSettings
{
    private static readonly OptimizationCriterion[] CanonicalCriteria =
    {
        OptimizationCriterion.CharacterSwitches,
        OptimizationCriterion.RetainerAccesses,
        OptimizationCriterion.ConsumedStacks,
        OptimizationCriterion.TransferHops,
        OptimizationCriterion.SourcePriority,
        OptimizationCriterion.Freshness,
        OptimizationCriterion.Alphabetical
    };

    private readonly IReadOnlyList<OptimizationCriterion> criteria;

    public IReadOnlyList<OptimizationCriterion> Criteria =>
        criteria;

    public OptimizationSettings()
    {
        criteria =
            CanonicalCriteria.ToArray();
    }

    public OptimizationSettings(
        IEnumerable<OptimizationCriterion> criteria)
    {
        var materialized =
            criteria.ToArray();

        if (!materialized.SequenceEqual(
                CanonicalCriteria))
        {
            throw new InvalidOperationException(
                "Optimization criteria must match the canonical FROG order exactly.");
        }

        this.criteria =
            materialized;
    }
}
