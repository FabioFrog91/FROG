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
        SourceDecrease != DestinationIncrease ||
        SourceDecrease > PlannedQuantity ||
        DestinationIncrease > PlannedQuantity;
}

/// <summary>
/// Explains an observed logical MOVE mismatch without mutating the immutable
/// planner decision. A quantity is transferred only when it is observed
/// leaving the logical source and entering the logical destination.
/// </summary>
public sealed class PlanExecutionReconciler
{
    public PlanExecutionReconciliationResult Reconcile(
        PlannerDecision decision,
        PlanExecutionBaseline baseline,
        PlanExecutionObservation observation)
    {
        if (decision.Type !=
                PlannerDecisionType.Move ||
            decision.Source is null ||
            decision.Destination is null)
        {
            return new PlanExecutionReconciliationResult(
                PlanExecutionReconciliationStatus.NotSatisfied,
                decision.Quantity,
                0,
                0,
                Math.Max(
                    0,
                    decision.Quantity),
                0,
                0,
                "La decisione non è un MOVE riconciliabile.");
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
                decision.Quantity,
                observedTransferredQuantity);

        var remainingQuantity =
            Math.Max(
                0,
                decision.Quantity -
                reconciledQuantity);

        if (reconciledQuantity >=
            decision.Quantity)
        {
            return new PlanExecutionReconciliationResult(
                PlanExecutionReconciliationStatus.Satisfied,
                decision.Quantity,
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
                decision.Quantity,
                reconciledQuantity,
                observedTransferredQuantity,
                remainingQuantity,
                sourceDecrease,
                destinationIncrease,
                $"Trasferimento parziale: osservate {reconciledQuantity} unità, ne mancano {remainingQuantity}.");
        }

        return new PlanExecutionReconciliationResult(
            PlanExecutionReconciliationStatus.NotSatisfied,
            decision.Quantity,
            0,
            observedTransferredQuantity,
            decision.Quantity,
            sourceDecrease,
            destinationIncrease,
            "Nessuna quantità trasferita è confermata contemporaneamente da source e destination.");
    }
}
