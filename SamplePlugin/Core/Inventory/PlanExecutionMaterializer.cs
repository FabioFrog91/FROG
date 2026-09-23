using Dalamud.Game.Inventory;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

public sealed record ExecutionInstruction(
    PlannerDecision Decision,
    int RemainingQuantity,
    IReadOnlyList<InventoryStackAllocation> SourceStacks)
{
    public int Quantity =>
        RemainingQuantity;
}

public sealed record PlanExecutionMaterializationResult(
    bool IsAvailable,
    ExecutionInstruction? Instruction,
    int AvailableQuantity,
    string Message)
{
    public static PlanExecutionMaterializationResult Available(
        ExecutionInstruction instruction) =>
        new(
            true,
            instruction,
            instruction.Quantity,
            string.Empty);

    public static PlanExecutionMaterializationResult Unavailable(
        int availableQuantity,
        string message) =>
        new(
            false,
            null,
            availableQuantity,
            message);
}

/// <summary>
/// Materializes one logical planner decision against the current observed
/// inventory state. It decides physical source stacks only; it never changes
/// route, item, quality or planned quantity.
/// </summary>
public sealed class PlanExecutionMaterializer
{
    private readonly ExecutionOrderCompiler executionOrderCompiler;

    public PlanExecutionMaterializer(
        ExecutionOrderCompiler executionOrderCompiler)
    {
        this.executionOrderCompiler =
            executionOrderCompiler;
    }

    public PlanExecutionMaterializationResult Materialize(
        PlannerDecision decision,
        IReadOnlyList<InventoryItemSnapshot> currentItems,
        int? requestedQuantity = null)
    {
        if (decision.Type ==
            PlannerDecisionType.SwitchCharacter)
        {
            return PlanExecutionMaterializationResult.Available(
                new ExecutionInstruction(
                    decision,
                    0,
                    Array.Empty<InventoryStackAllocation>()));
        }

        var quantity =
            requestedQuantity ??
            decision.Quantity;

        if (decision.Source is null ||
            decision.Destination is null ||
            quantity <= 0 ||
            quantity > decision.Quantity)
        {
            return PlanExecutionMaterializationResult.Unavailable(
                0,
                "Decisione MOVE logica non valida.");
        }

        var matchingStacks =
            currentItems
                .Where(item =>
                    IsSameLogicalSource(
                        item,
                        decision.Source) &&
                    IsExecutionContainer(
                        item) &&
                    item.BaseItemId ==
                        decision.BaseItemId &&
                    item.IsHq ==
                        decision.IsHq &&
                    item.Quantity > 0)
                .ToList();

        var availableQuantity =
            matchingStacks.Sum(item =>
                item.Quantity);

        if (availableQuantity <
            quantity)
        {
            return PlanExecutionMaterializationResult.Unavailable(
                availableQuantity,
                $"La source logica contiene {availableQuantity} unità, ma l'esecuzione residua ne richiede {quantity}.");
        }

        var orderedStacks =
            executionOrderCompiler
                .OrderStacksForExecution(
                    matchingStacks);

        var remaining =
            quantity;

        var allocations =
            new List<InventoryStackAllocation>();

        foreach (var stack in orderedStacks)
        {
            if (remaining <= 0)
                break;

            var quantity =
                Math.Min(
                    remaining,
                    stack.Quantity);

            allocations.Add(
                new InventoryStackAllocation(
                    stack,
                    quantity));

            remaining -=
                quantity;
        }

        if (remaining != 0)
        {
            return PlanExecutionMaterializationResult.Unavailable(
                quantity -
                remaining,
                "La materializzazione fisica non copre tutta la quantità logica pianificata.");
        }

        return PlanExecutionMaterializationResult.Available(
            new ExecutionInstruction(
                decision,
                quantity,
                allocations));
    }

    private static bool IsExecutionContainer(
        InventoryItemSnapshot item) =>
        item.Storage switch
        {
            StorageType.CharacterInventory =>
                item.Container is
                    (uint)GameInventoryType.Inventory1 or
                    (uint)GameInventoryType.Inventory2 or
                    (uint)GameInventoryType.Inventory3 or
                    (uint)GameInventoryType.Inventory4,

            StorageType.Retainer =>
                item.Container >=
                    (uint)GameInventoryType.RetainerPage1 &&
                item.Container <=
                    (uint)GameInventoryType.RetainerPage7,

            StorageType.FreeCompanyChest =>
                item.Container >=
                    (uint)GameInventoryType.FreeCompanyPage1 &&
                item.Container <=
                    (uint)GameInventoryType.FreeCompanyPage5,

            _ => false
        };

    private static bool IsSameLogicalSource(
        InventoryItemSnapshot item,
        PlannerLogicalSource source) =>
        item.Storage ==
            source.Storage &&
        item.OwnerId ==
            source.OwnerId &&
        (source.Storage != StorageType.Retainer ||
         item.ParentCharacterId ==
            source.ParentCharacterId);
}
