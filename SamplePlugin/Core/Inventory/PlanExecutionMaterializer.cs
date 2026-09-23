using System;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

public sealed record ExecutionInstruction(
    PlannerDecision Decision,
    IReadOnlyList<InventoryStackAllocation> SourceStacks)
{
    public int Quantity =>
        Decision.Quantity;
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
        IReadOnlyList<InventoryItemSnapshot> currentItems)
    {
        if (decision.Type ==
            PlannerDecisionType.SwitchCharacter)
        {
            return PlanExecutionMaterializationResult.Available(
                new ExecutionInstruction(
                    decision,
                    Array.Empty<InventoryStackAllocation>()));
        }

        if (decision.Source is null ||
            decision.Destination is null ||
            decision.Quantity <= 0)
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
            decision.Quantity)
        {
            return PlanExecutionMaterializationResult.Unavailable(
                availableQuantity,
                $"La source logica contiene {availableQuantity} unità, ma il piano ne richiede {decision.Quantity}.");
        }

        var orderedStacks =
            executionOrderCompiler
                .OrderStacksForExecution(
                    matchingStacks);

        var remaining =
            decision.Quantity;

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
                decision.Quantity -
                remaining,
                "La materializzazione fisica non copre tutta la quantità logica pianificata.");
        }

        return PlanExecutionMaterializationResult.Available(
            new ExecutionInstruction(
                decision,
                allocations));
    }

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
