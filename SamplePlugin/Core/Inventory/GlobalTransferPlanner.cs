using FROG.Core.Inventory;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SamplePlugin.Core.Inventory;

public sealed class GlobalTransferPlanner
{
    private readonly PlannerActionValidator actionValidator;
    private readonly PlannerPlanEvaluator planEvaluator;

    public GlobalTransferPlanner()
    {
        actionValidator = new PlannerActionValidator();
        planEvaluator = new PlannerPlanEvaluator();
    }

    public PlannerPlan Plan(
        RequirementSet requirements,
        PlannerState initialState,
        ResolutionPolicy resolutionPolicy,
        OptimizationSettings optimizationSettings)
    {
        var initialPlan = new PlannerPlan(
            initialState,
            initialState,
            Array.Empty<PlannerAction>(),
            PlannerPlanResult.CompletedWithMissing,
            CalculateMissing(requirements, initialState));

        var bestPlan = initialPlan;

        Search(
            requirements,
            resolutionPolicy,
            optimizationSettings,
            initialState,
            initialPlan,
            new HashSet<string>(),
            ref bestPlan);

        var missing = CalculateMissing(
            requirements,
            bestPlan.FinalState);

        return bestPlan.WithResult(
            missing == 0
                ? PlannerPlanResult.Completed
                : PlannerPlanResult.CompletedWithMissing,
            missing);
    }

    private void Search(
        RequirementSet requirements,
        ResolutionPolicy resolutionPolicy,
        OptimizationSettings optimizationSettings,
        PlannerState state,
        PlannerPlan plan,
        HashSet<string> visitedStates,
        ref PlannerPlan bestPlan)
    {
        var stateKey = BuildStateKey(
            requirements,
            state);

        if (!visitedStates.Add(stateKey))
            return;

        var currentMissing = CalculateMissing(
            requirements,
            state);

        var bestMissing = CalculateMissing(
            requirements,
            bestPlan.FinalState);

        if (currentMissing < bestMissing)
        {
            bestPlan = plan.WithResult(
                currentMissing == 0
                    ? PlannerPlanResult.Completed
                    : PlannerPlanResult.CompletedWithMissing,
                currentMissing);
        }
        else if (currentMissing == bestMissing)
        {
            var currentScore = planEvaluator.Evaluate(
                plan,
                resolutionPolicy);

            var bestScore = planEvaluator.Evaluate(
                bestPlan,
                resolutionPolicy);

            if (currentScore.CompareTo(
                    bestScore,
                    optimizationSettings) < 0)
            {
                bestPlan = plan.WithResult(
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
                nextState = Apply(state, action);
            }
            catch
            {
                continue;
            }

            var nextPlan = plan.Append(
                action,
                nextState);

            Search(
                requirements,
                resolutionPolicy,
                optimizationSettings,
                nextState,
                nextPlan,
                new HashSet<string>(visitedStates),
                ref bestPlan);
        }
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
                foreach (var isHq in GetAllowedQualities(
                             requirement.QualityPolicy,
                             state,
                             requirement.BaseItemId,
                             source))
                {
                    var available = state.GetQuantity(
                        requirement.BaseItemId,
                        isHq,
                        source);

                    if (available <= 0)
                        continue;

                    var mainQuantity = state.GetMainInventoryQuantity(
                        requirement.BaseItemId,
                        isHq);

                    var needed = Math.Max(
                        0,
                        requirement.Quantity - mainQuantity);

                    if (needed <= 0)
                        continue;

                    var quantity = Math.Min(
                        available,
                        needed);

                    if (quantity <= 0)
                        continue;

                    var destination = GetImmediateDestination(
                        state.CurrentCharacterId,
                        source);

                    if (destination is not null)
                    {
                        yield return PlannerAction.Move(
                            source,
                            destination,
                            requirement.BaseItemId,
                            isHq,
                            quantity);
                    }
                }
            }
        }

        var targetCharacters = resolutionPolicy.Sources
            .Select(GetCharacterId)
            .Where(id =>
                id != 0 &&
                id != state.CurrentCharacterId)
            .Distinct()
            .OrderBy(id => id);

        foreach (var targetCharacter in targetCharacters)
        {
            yield return PlannerAction.SwitchCharacter(
                state.CurrentCharacterId,
                targetCharacter);
        }
    }

    private static IEnumerable<bool> GetAllowedQualities(
        RequirementQualityPolicy policy,
        PlannerState state,
        uint baseItemId,
        InventorySource source)
    {
        switch (policy)
        {
            case RequirementQualityPolicy.HqOnly:
                if (state.GetQuantity(baseItemId, true, source) > 0)
                    yield return true;
                yield break;

            case RequirementQualityPolicy.NqOnly:
                if (state.GetQuantity(baseItemId, false, source) > 0)
                    yield return false;
                yield break;

            case RequirementQualityPolicy.HqFirst:
                if (state.GetQuantity(baseItemId, true, source) > 0)
                    yield return true;

                if (state.GetQuantity(baseItemId, false, source) > 0)
                    yield return false;

                yield break;

            case RequirementQualityPolicy.NqFirst:
                if (state.GetQuantity(baseItemId, false, source) > 0)
                    yield return false;

                if (state.GetQuantity(baseItemId, true, source) > 0)
                    yield return true;

                yield break;

            case RequirementQualityPolicy.Any:
                if (state.GetQuantity(baseItemId, false, source) > 0)
                    yield return false;

                if (state.GetQuantity(baseItemId, true, source) > 0)
                    yield return true;

                yield break;
        }
    }

    private static InventorySource? GetImmediateDestination(
        ulong currentCharacterId,
        InventorySource source)
    {
        return source.Storage switch
        {
            StorageType.Retainer =>
                new InventorySource(
                    StorageType.CharacterInventory,
                    source.ParentCharacterId,
                    0),

            StorageType.CharacterInventory =>
                null,

            StorageType.FreeCompanyChest =>
                new InventorySource(
                    StorageType.CharacterInventory,
                    currentCharacterId,
                    0),

            _ => null
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

            _ => 0
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
        var missing = 0;

        foreach (var requirement in requirements.Requirements)
        {
            var mainQuantity = GetRequirementQuantityInMain(
                requirement,
                state);

            missing += Math.Max(
                0,
                requirement.Quantity - mainQuantity);
        }

        return missing;
    }

    private static int GetRequirementQuantityInMain(
        Requirement requirement,
        PlannerState state)
    {
        var hq = state.GetMainInventoryQuantity(
            requirement.BaseItemId,
            true);

        var nq = state.GetMainInventoryQuantity(
            requirement.BaseItemId,
            false);

        return requirement.QualityPolicy switch
        {
            RequirementQualityPolicy.HqOnly => hq,

            RequirementQualityPolicy.NqOnly => nq,

            RequirementQualityPolicy.HqFirst =>
                Math.Min(requirement.Quantity, hq) +
                Math.Min(
                    requirement.Quantity -
                    Math.Min(requirement.Quantity, hq),
                    nq),

            RequirementQualityPolicy.NqFirst =>
                Math.Min(requirement.Quantity, nq) +
                Math.Min(
                    requirement.Quantity -
                    Math.Min(requirement.Quantity, nq),
                    hq),

            RequirementQualityPolicy.Any =>
                hq + nq,

            _ => 0
        };
    }

    private static string BuildStateKey(
        RequirementSet requirements,
        PlannerState state)
    {
        var requirementKey = string.Join(
            ";",
            requirements.Requirements.Select(requirement =>
                $"{requirement.BaseItemId}:" +
                $"{requirement.Quantity}:" +
                $"{requirement.QualityPolicy}"));

        var itemKey = string.Join(
            ";",
            state.Items
                .OrderBy(item => item.Storage)
                .ThenBy(item => item.OwnerId)
                .ThenBy(item => item.Container)
                .ThenBy(item => item.Slot)
                .ThenBy(item => item.BaseItemId)
                .ThenBy(item => item.IsHq)
                .Select(item =>
                    $"{item.Storage}:" +
                    $"{item.OwnerId}:" +
                    $"{item.Container}:" +
                    $"{item.Slot}:" +
                    $"{item.BaseItemId}:" +
                    $"{item.IsHq}:" +
                    $"{item.Quantity}"));

        return $"{state.CurrentCharacterId}|{requirementKey}|{itemKey}";
    }
}
