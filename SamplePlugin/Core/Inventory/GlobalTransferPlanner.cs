using System;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

public sealed class GlobalTransferPlanner
{
    private readonly PlannerActionValidator actionValidator = new();
    private readonly PlannerPlanEvaluator planEvaluator = new();

    public PlannerPlan Plan(
        RequirementSet requirements,
        PlannerState initialState,
        ResolutionPolicy resolutionPolicy,
        OptimizationSettings optimizationSettings)
    {
        var initialMissing = CalculateMissing(requirements, initialState);

        var initialPlan = new PlannerPlan(
            initialState,
            initialState,
            Array.Empty<PlannerAction>(),
            initialMissing == 0
                ? PlannerPlanResult.Completed
                : PlannerPlanResult.CompletedWithMissing,
            initialMissing);

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
        var stateKey = BuildStateKey(state);

        if (!visitedStates.Add(stateKey))
            return;

        var currentMissing = CalculateMissing(
            requirements,
            state);

        var bestMissing = CalculateMissing(
            requirements,
            bestPlan.FinalState);

        if (currentMissing < bestMissing ||
            currentMissing == bestMissing &&
            planEvaluator.Evaluate(
                    plan,
                    requirements,
                    resolutionPolicy)
                .CompareTo(
                    planEvaluator.Evaluate(
                        bestPlan,
                        requirements,
                        resolutionPolicy),
                    optimizationSettings) < 0)
        {
            bestPlan = plan.WithResult(
                currentMissing == 0
                    ? PlannerPlanResult.Completed
                    : PlannerPlanResult.CompletedWithMissing,
                currentMissing);
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
            catch (InvalidOperationException)
            {
                continue;
            }

            Search(
                requirements,
                resolutionPolicy,
                optimizationSettings,
                nextState,
                plan.Append(action, nextState),
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
                if (!IsSourceAccessible(state.CurrentCharacterId, source))
                    continue;

                foreach (var isHq in GetCandidateQualities(
                             requirement,
                             state,
                             source))
                {
                    var available = state.GetQuantity(
                        requirement.BaseItemId,
                        isHq,
                        source);

                    if (available <= 0)
                        continue;

                    var needed = GetNeededFromSource(
                        requirement,
                        state,
                        source,
                        isHq);

                    if (needed <= 0)
                        continue;

                    var destination = GetDestination(
                        state,
                        resolutionPolicy,
                        source);

                    if (destination is null)
                        continue;

                    var quantity = Math.Min(
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
        var qualities = requirement.QualityPolicy switch
        {
            RequirementQualityPolicy.HqOnly => new[] { true },
            RequirementQualityPolicy.NqOnly => new[] { false },
            RequirementQualityPolicy.HqFirst => new[] { true, false },
            RequirementQualityPolicy.NqFirst => new[] { false, true },
            _ => new[] { false, true }
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
        var includeFreeCompany = source.Storage switch
        {
            StorageType.Retainer =>
                source.ParentCharacterId != state.MainCharacterId,

            StorageType.CharacterInventory =>
                source.OwnerId != state.MainCharacterId,

            _ => false
        };

        var includeLocalInventory = source.Storage == StorageType.Retainer &&
                                    source.ParentCharacterId != state.MainCharacterId;

        var hq = state.GetMainInventoryQuantity(
            requirement.BaseItemId,
            true);

        var nq = state.GetMainInventoryQuantity(
            requirement.BaseItemId,
            false);

        if (includeFreeCompany)
        {
            hq += state.GetFreeCompanyQuantity(
                requirement.BaseItemId,
                true);

            nq += state.GetFreeCompanyQuantity(
                requirement.BaseItemId,
                false);
        }

        if (includeLocalInventory)
        {
            hq += state.GetCharacterInventoryQuantity(
                source.ParentCharacterId,
                requirement.BaseItemId,
                true);

            nq += state.GetCharacterInventoryQuantity(
                source.ParentCharacterId,
                requirement.BaseItemId,
                false);
        }

        return requirement.QualityPolicy switch
        {
            RequirementQualityPolicy.HqOnly =>
                isHq
                    ? Math.Max(0, requirement.Quantity - hq)
                    : 0,

            RequirementQualityPolicy.NqOnly =>
                !isHq
                    ? Math.Max(0, requirement.Quantity - nq)
                    : 0,

            RequirementQualityPolicy.HqFirst =>
                isHq
                    ? Math.Max(0, requirement.Quantity - hq)
                    : Math.Max(0, requirement.Quantity - hq - nq),

            RequirementQualityPolicy.NqFirst =>
                !isHq
                    ? Math.Max(0, requirement.Quantity - nq)
                    : Math.Max(0, requirement.Quantity - nq - hq),

            RequirementQualityPolicy.Any =>
                Math.Max(0, requirement.Quantity - hq - nq),

            _ => 0
        };
    }

    private static bool IsSourceAccessible(
        ulong currentCharacterId,
        InventorySource source) =>
        source.Storage switch
        {
            StorageType.CharacterInventory =>
                source.OwnerId == currentCharacterId,

            StorageType.Retainer =>
                source.ParentCharacterId == currentCharacterId,

            StorageType.FreeCompanyChest =>
                true,

            _ => false
        };

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
        ulong characterId) =>
        resolutionPolicy.Sources
            .Where(source =>
                source.Storage == StorageType.CharacterInventory &&
                source.OwnerId == characterId)
            .OrderBy(source => source.Container)
            .FirstOrDefault();

    private static InventorySource? FindFreeCompany(
        ResolutionPolicy resolutionPolicy,
        ulong characterId) =>
        resolutionPolicy.Sources
            .Where(source =>
                source.Storage == StorageType.FreeCompanyChest &&
                (source.ParentCharacterId == characterId ||
                 source.ParentCharacterId == 0))
            .OrderBy(source => source.Container)
            .FirstOrDefault();

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
            if (state.HasVisitedCharacter(characterId))
                continue;

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

            if (source.Storage == StorageType.FreeCompanyChest)
                continue;

            foreach (var requirement in requirements.Requirements)
            {
                if (source.Storage == StorageType.CharacterInventory &&
                    source.OwnerId == characterId &&
                    source.OwnerId == state.MainCharacterId)
                {
                    continue;
                }

                if (GetAllowedQualities(requirement)
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
            GetAllowedQualities(requirement)
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
            RequirementQualityPolicy.HqOnly => new[] { true },
            RequirementQualityPolicy.NqOnly => new[] { false },
            RequirementQualityPolicy.HqFirst => new[] { true, false },
            RequirementQualityPolicy.NqFirst => new[] { false, true },
            _ => new[] { false, true }
        };
    }

    private static ulong GetCharacterId(
        InventorySource source) =>
        source.Storage switch
        {
            StorageType.CharacterInventory =>
                source.OwnerId,

            StorageType.Retainer =>
                source.ParentCharacterId,

            StorageType.FreeCompanyChest =>
                source.ParentCharacterId,

            _ => 0
        };

    private static PlannerState Apply(
        PlannerState state,
        PlannerAction action) =>
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

            _ => throw new InvalidOperationException(
                "Unknown planner action.")
        };

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
        var hq = state.GetMainInventoryQuantity(
            requirement.BaseItemId,
            true);

        var nq = state.GetMainInventoryQuantity(
            requirement.BaseItemId,
            false);

        return requirement.QualityPolicy switch
        {
            RequirementQualityPolicy.HqOnly =>
                hq,

            RequirementQualityPolicy.NqOnly =>
                nq,

            RequirementQualityPolicy.HqFirst =>
                Math.Min(requirement.Quantity, hq) +
                Math.Min(
                    Math.Max(0, requirement.Quantity - hq),
                    nq),

            RequirementQualityPolicy.NqFirst =>
                Math.Min(requirement.Quantity, nq) +
                Math.Min(
                    Math.Max(0, requirement.Quantity - nq),
                    hq),

            RequirementQualityPolicy.Any =>
                Math.Min(
                    requirement.Quantity,
                    hq + nq),

            _ => 0
        };
    }

    private static string BuildStateKey(PlannerState state)
    {
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
                    $"{item.Storage}:{item.OwnerId}:{item.Container}:{item.Slot}:" +
                    $"{item.BaseItemId}:{item.IsHq}:{item.Quantity}"));

        var visitedKey = string.Join(
            ",",
            state.VisitedCharacters.OrderBy(id => id));

        return $"{state.MainCharacterId}|{state.CurrentCharacterId}|{visitedKey}|{itemKey}";
    }
}
