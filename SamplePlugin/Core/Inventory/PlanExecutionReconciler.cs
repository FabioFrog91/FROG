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
    int ObservedTransferredQuantity,
    int RemainingQuantity,
    int SourceDecrease,
    int DestinationIncrease,
    string Message)
{
    public int VarianceQuantity =>
        ObservedTransferredQuantity - PlannedQuantity;

    public bool HasVariance =>
        SourceDecrease != PlannedQuantity ||
        DestinationIncrease != PlannedQuantity;
}

/// <summary>
/// Explains an observed MOVE mismatch without mutating the immutable plan.
/// The reconciled quantity is intentionally conservative: material counts as
/// transferred only when it is observed leaving the source and entering the
/// destination.
/// </summary>
public sealed class PlanExecutionReconciler
{
    public PlanExecutionReconciliationResult ReconcileSourceContainerChange(
        PlannerAction action) =>
        new(
            PlanExecutionReconciliationStatus.NotSatisfied,
            action.Quantity,
            0,
            0,
            Math.Max(
                0,
                action.Quantity),
            0,
            0,
            "Lo stack sorgente è stato spostato in un altro contenitore. Piano fisico da ricalcolare.");

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

        var observedTransferredQuantity =
            Math.Min(
                sourceDecrease,
                destinationIncrease);

        var reconciledQuantity =
            Math.Min(
                action.Quantity,
                observedTransferredQuantity);

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
                observedTransferredQuantity,
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
                observedTransferredQuantity,
                remainingQuantity,
                sourceDecrease,
                destinationIncrease,
                $"Trasferimento parziale: osservate {reconciledQuantity} unità, ne mancano {remainingQuantity}.");
        }

        return new PlanExecutionReconciliationResult(
            PlanExecutionReconciliationStatus.NotSatisfied,
            action.Quantity,
            0,
            observedTransferredQuantity,
            action.Quantity,
            sourceDecrease,
            destinationIncrease,
            "Nessuna quantità trasferita è confermata contemporaneamente da source e destination.");
    }
}
