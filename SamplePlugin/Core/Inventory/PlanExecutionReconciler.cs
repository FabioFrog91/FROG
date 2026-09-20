using System;

namespace FROG.Core.Inventory;

public enum PlanExecutionReconciliationStatus
{
    Satisfied,
    PartiallySatisfied,
    NotSatisfied
}

public sealed record PlanExecutionReconciliationResult(
    PlanExecutionReconciliationStatus Status,
    int PlannedQuantity,
    int ReconciledQuantity,
    int RemainingQuantity,
    int SourceDecrease,
    int DestinationIncrease,
    string Message);

/// <summary>
/// Explains an observed MOVE mismatch without mutating the immutable plan.
/// The reconciled quantity is intentionally conservative: material counts as
/// transferred only when it is observed leaving the source and entering the
/// destination.
/// </summary>
public sealed class PlanExecutionReconciler
{
    public PlanExecutionReconciliationResult Reconcile(
        PlannerAction action,
        PlanExecutionBaseline baseline,
        PlanExecutionObservation observation)
    {
        if (action.Type != PlannerActionType.Move ||
            action.Source is null ||
            action.Destination is null)
        {
            return new PlanExecutionReconciliationResult(
                PlanExecutionReconciliationStatus.NotSatisfied,
                action.Quantity,
                0,
                Math.Max(0, action.Quantity),
                0,
                0,
                "L'azione non è un MOVE riconciliabile.");
        }

        var sourceDecrease =
            Math.Max(
                0,
                baseline.SourceQuantity -
                observation.SourceQuantity);

        var destinationIncrease =
            Math.Max(
                0,
                observation.DestinationQuantity -
                baseline.DestinationQuantity);

        var reconciledQuantity =
            Math.Min(
                action.Quantity,
                Math.Min(
                    sourceDecrease,
                    destinationIncrease));

        var remainingQuantity =
            Math.Max(
                0,
                action.Quantity -
                reconciledQuantity);

        if (reconciledQuantity >= action.Quantity)
        {
            return new PlanExecutionReconciliationResult(
                PlanExecutionReconciliationStatus.Satisfied,
                action.Quantity,
                reconciledQuantity,
                0,
                sourceDecrease,
                destinationIncrease,
                "Il trasferimento pianificato risulta soddisfatto dai delta osservati.");
        }

        if (reconciledQuantity > 0)
        {
            return new PlanExecutionReconciliationResult(
                PlanExecutionReconciliationStatus.PartiallySatisfied,
                action.Quantity,
                reconciledQuantity,
                remainingQuantity,
                sourceDecrease,
                destinationIncrease,
                $"Trasferimento parziale: osservate {reconciledQuantity} unità, ne mancano {remainingQuantity}.");
        }

        return new PlanExecutionReconciliationResult(
            PlanExecutionReconciliationStatus.NotSatisfied,
            action.Quantity,
            0,
            action.Quantity,
            sourceDecrease,
            destinationIncrease,
            "Nessuna quantità trasferita è confermata contemporaneamente da source e destination.");
    }
}
