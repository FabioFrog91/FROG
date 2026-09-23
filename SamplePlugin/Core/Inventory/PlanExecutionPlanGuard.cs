using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

public sealed record PlanExecutionPlanValidityResult(
    bool IsValid,
    string Message)
{
    public static PlanExecutionPlanValidityResult Valid { get; } =
        new(
            true,
            string.Empty);
}

/// <summary>
/// Preflights only the current logical planner decision against the latest
/// observed state. Future decisions are validated when they become current.
/// </summary>
public sealed class PlanExecutionPlanGuard
{
    public PlanExecutionPlanValidityResult ValidateCurrent(
        PlannerPlan plan,
        PlannerDecision decision,
        ulong currentCharacterId,
        IReadOnlyList<InventoryItemSnapshot> currentItems)
    {
        if (currentCharacterId == 0)
        {
            return Invalid(
                "Personaggio corrente non disponibile.");
        }

        if (decision.Type ==
            PlannerDecisionType.SwitchCharacter)
        {
            if (decision.FromCharacterId == 0 ||
                decision.ToCharacterId == 0)
            {
                return Invalid(
                    "Cambio personaggio privo di ID validi.");
            }

            if (decision.FromCharacterId !=
                currentCharacterId)
            {
                return Invalid(
                    "Il personaggio corrente non corrisponde alla source dello switch.");
            }

            if (decision.FromCharacterId ==
                decision.ToCharacterId)
            {
                return Invalid(
                    "Il cambio personaggio non può avere source e target uguali.");
            }

            return PlanExecutionPlanValidityResult.Valid;
        }

        if (decision.Source is null ||
            decision.Destination is null)
        {
            return Invalid(
                "MOVE logico privo di source o destination.");
        }

        if (decision.Quantity <= 0)
        {
            return Invalid(
                "La quantità del MOVE deve essere maggiore di zero.");
        }

        if (decision.Source ==
            decision.Destination)
        {
            return Invalid(
                "Source e destination non possono coincidere.");
        }

        if (!InventoryRouteRules.IsCharacterAccessible(
                currentCharacterId,
                decision.Source))
        {
            return Invalid(
                "La source non è accessibile dal personaggio corrente.");
        }

        if (!InventoryRouteRules.IsCharacterAccessible(
                currentCharacterId,
                decision.Destination))
        {
            return Invalid(
                "La destination non è accessibile dal personaggio corrente.");
        }

        if (!InventoryRouteRules.IsLegalRoute(
                decision.Source,
                decision.Destination))
        {
            return Invalid(
                "La route storage-to-storage non è valida.");
        }

        var sourceQuantity =
            currentItems
                .Where(item =>
                    item.BaseItemId ==
                        decision.BaseItemId &&
                    item.IsHq ==
                        decision.IsHq &&
                    item.Storage ==
                        decision.Source.Storage &&
                    ExecutionInventoryRules.IsExecutableContainer(
                        item) &&
                    item.OwnerId ==
                        decision.Source.OwnerId &&
                    (decision.Source.Storage !=
                         StorageType.Retainer ||
                     item.ParentCharacterId ==
                         decision.Source.ParentCharacterId))
                .Sum(item =>
                    item.Quantity);

        if (sourceQuantity <
            decision.Quantity)
        {
            return Invalid(
                $"La source logica contiene {sourceQuantity} unità, ma il piano ne richiede {decision.Quantity}.");
        }

        var capacity =
            plan.FinalState.Capacity.Rebase(
                currentItems);

        if (capacity.GetAcceptableQuantity(
                decision.Destination,
                decision.BaseItemId,
                decision.IsHq,
                decision.Quantity) <
            decision.Quantity)
        {
            return Invalid(
                "La destination non dispone più della capacità logica necessaria.");
        }

        return PlanExecutionPlanValidityResult.Valid;
    }

    private static PlanExecutionPlanValidityResult Invalid(
        string message) =>
        new(
            false,
            message);
}
