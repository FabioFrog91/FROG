using System;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

/// <summary>
/// Checks whether observed inventory plus the remaining logical decisions can
/// still preserve material already delivered along the route. This does not
/// replay physical actions or decide a replacement route.
/// </summary>
internal sealed class PlanExecutionProgressGuard
{
    private readonly PlannerPlan plan;
    private readonly RequirementSet requirements;
    private readonly ulong mainCharacterId;

    public PlanExecutionProgressGuard(
        PlannerPlan plan,
        RequirementSet requirements)
    {
        this.plan = plan;
        this.requirements = requirements;
        mainCharacterId = plan.InitialState.MainCharacterId;
    }

    public string? FindRegression(
        PlanExecutionSession session,
        IReadOnlyList<InventoryItemSnapshot> observedItems)
    {
        var remaining = plan.Decisions
            .Skip(session.VerifiedDecisionCount)
            .Where(decision =>
                decision.Type == PlannerDecisionType.Move &&
                decision.Source is not null &&
                decision.Destination is not null)
            .ToArray();

        // Only intermediate destinations already reached by this session need
        // protection. A future destination can legitimately be empty today.
        var reached = plan.Decisions
            .Take(session.VerifiedDecisionCount)
            .Where(decision =>
                decision.Type == PlannerDecisionType.Move &&
                decision.Destination is not null)
            .Select(decision =>
                Key(decision.Destination!, decision.BaseItemId, decision.IsHq))
            .Distinct();

        foreach (var key in reached)
        {
            if (key.Storage == StorageType.CharacterInventory &&
                key.OwnerId == mainCharacterId)
                continue;

            if (TouchesCurrentMove(session.CurrentDecision, key))
                continue;

            // A shared FC stack has no provenance. Only the aggregate amount
            // needed before later deposits is relevant, regardless of owner.
            var needed = NeededBeforeFutureSupply(remaining, key);
            if (needed <= 0)
                continue;

            var available = Quantity(observedItems, key);
            if (available < needed)
            {
                return $"La tappa {key.Storage}/{key.OwnerId} contiene " +
                    $"{available} unità di {key.BaseItemId} " +
                    $"{(key.IsHq ? "HQ" : "NQ")}, ma le decisioni residue " +
                    $"ne richiedono {needed} prima della prossima consegna.";
            }
        }

        // The main's requirement is the final destination. Compare coverage,
        // not raw stacks: Any/HqFirst/NqFirst may be met with either quality.
        foreach (var requirement in requirements.Requirements)
        {
            if (TouchesCurrentMainMove(session.CurrentDecision,
                    requirement.BaseItemId))
            {
                continue;
            }

            var hqKey = new LogicalKey(
                StorageType.CharacterInventory, mainCharacterId,
                requirement.BaseItemId, true);
            var nqKey = hqKey with { IsHq = false };

            var expected = Coverage(requirement,
                Quantity(plan.FinalState.Items, hqKey),
                Quantity(plan.FinalState.Items, nqKey));
            var projected = Coverage(requirement,
                Quantity(observedItems, hqKey) +
                    RemainingDelta(remaining, hqKey),
                Quantity(observedItems, nqKey) +
                    RemainingDelta(remaining, nqKey));

            if (projected < expected)
            {
                return $"Il requisito {requirement.BaseItemId} del main " +
                    $"non è più coperto: proiezione {projected}, " +
                    $"piano {expected}.";
            }
        }

        return null;
    }

    private static int Coverage(
        Requirement requirement,
        int hq,
        int nq) =>
        Math.Min(requirement.Quantity,
            Math.Max(0,
                GlobalAllocationPlanner.GetSatisfiedMainQuantity(
                    requirement, Math.Max(0, hq), Math.Max(0, nq))));

    private static int NeededBeforeFutureSupply(
        IEnumerable<PlannerDecision> remaining,
        LogicalKey key)
    {
        var balance = 0;
        var needed = 0;
        foreach (var decision in remaining)
        {
            if (Key(decision.Source!, decision.BaseItemId,
                    decision.IsHq) == key)
                balance -= decision.Quantity;

            if (Key(decision.Destination!, decision.BaseItemId,
                    decision.IsHq) == key)
                balance += decision.Quantity;

            needed = Math.Max(needed, -balance);
        }

        return needed;
    }

    private static int Quantity(
        IEnumerable<InventoryItemSnapshot> items,
        LogicalKey key) =>
        items.Where(item =>
                item.Storage == key.Storage &&
                item.OwnerId == key.OwnerId &&
                item.BaseItemId == key.BaseItemId &&
                item.IsHq == key.IsHq &&
                ExecutionInventoryRules.IsExecutableContainer(item))
            .Sum(item => item.Quantity);

    private static int RemainingDelta(
        IEnumerable<PlannerDecision> remaining,
        LogicalKey key)
    {
        var delta = 0;
        foreach (var decision in remaining)
        {
            if (Key(decision.Destination!, decision.BaseItemId,
                    decision.IsHq) == key)
                delta += decision.Quantity;

            if (Key(decision.Source!, decision.BaseItemId,
                    decision.IsHq) == key)
                delta -= decision.Quantity;
        }

        return delta;
    }

    private static LogicalKey Key(
        PlannerLogicalSource source,
        uint baseItemId,
        bool isHq) =>
        new(source.Storage, source.OwnerId, baseItemId, isHq);

    private static bool TouchesCurrentMove(
        PlannerDecision? current,
        LogicalKey key) =>
        current?.Type == PlannerDecisionType.Move &&
        current.BaseItemId == key.BaseItemId &&
        current.IsHq == key.IsHq &&
        (current.Source is not null &&
            Key(current.Source, current.BaseItemId, current.IsHq) == key ||
         current.Destination is not null &&
            Key(current.Destination, current.BaseItemId, current.IsHq) == key);

    private bool TouchesCurrentMainMove(
        PlannerDecision? current,
        uint baseItemId) =>
        current?.Type == PlannerDecisionType.Move &&
        current.BaseItemId == baseItemId &&
        (current.Source?.Storage == StorageType.CharacterInventory &&
            current.Source.OwnerId == mainCharacterId ||
         current.Destination?.Storage == StorageType.CharacterInventory &&
            current.Destination.OwnerId == mainCharacterId);

    private readonly record struct LogicalKey(
        StorageType Storage,
        ulong OwnerId,
        uint BaseItemId,
        bool IsHq);
}
