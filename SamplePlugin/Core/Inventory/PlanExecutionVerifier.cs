using System;

namespace FROG.Core.Inventory;

public enum PlanExecutionVerificationStatus
{
    WaitingForExecution,
    WaitingForObservation,
    Verified,
    Mismatch
}

public sealed record PlanExecutionBaseline(
    int ActionIndex,
    int SourceQuantity,
    int DestinationQuantity,
    DateTime? SourceObservedAtUtc,
    DateTime? DestinationObservedAtUtc);

public sealed record PlanExecutionVerificationResult(
    PlanExecutionVerificationStatus Status,
    string Message);

public sealed class PlanExecutionVerifier
{
    public PlanExecutionBaseline CaptureBaseline(
        int actionIndex,
        PlannerAction action,
        InventoryIndex inventoryIndex)
    {
        if (action.Type == PlannerActionType.SwitchCharacter ||
            action.Source is null ||
            action.Destination is null)
        {
            return new PlanExecutionBaseline(
                actionIndex,
                0,
                0,
                null,
                null);
        }

        return new PlanExecutionBaseline(
            actionIndex,
            inventoryIndex.GetQuantity(
                action.BaseItemId,
                action.IsHq,
                action.Source),
            inventoryIndex.GetQuantity(
                action.BaseItemId,
                action.IsHq,
                action.Destination),
            inventoryIndex.GetSourceObservedAtUtc(
                action.Source),
            inventoryIndex.GetSourceObservedAtUtc(
                action.Destination));
    }

    public PlanExecutionVerificationResult Verify(
        PlannerAction action,
        PlanExecutionBaseline baseline,
        InventoryIndex inventoryIndex,
        ulong currentCharacterId)
    {
        if (action.Type == PlannerActionType.SwitchCharacter)
        {
            return currentCharacterId == action.ToCharacterId
                ? new PlanExecutionVerificationResult(
                    PlanExecutionVerificationStatus.Verified,
                    "Cambio personaggio osservato.")
                : new PlanExecutionVerificationResult(
                    PlanExecutionVerificationStatus.WaitingForObservation,
                    "In attesa del personaggio previsto.");
        }

        if (action.Source is null ||
            action.Destination is null)
        {
            return new PlanExecutionVerificationResult(
                PlanExecutionVerificationStatus.Mismatch,
                "MOVE privo di source o destination.");
        }

        var sourceObservedAtUtc =
            inventoryIndex.GetSourceObservedAtUtc(
                action.Source);

        var destinationObservedAtUtc =
            inventoryIndex.GetSourceObservedAtUtc(
                action.Destination);

        if (!IsNewerObservation(
                sourceObservedAtUtc,
                baseline.SourceObservedAtUtc) ||
            !IsNewerObservation(
                destinationObservedAtUtc,
                baseline.DestinationObservedAtUtc))
        {
            return new PlanExecutionVerificationResult(
                PlanExecutionVerificationStatus.WaitingForObservation,
                "In attesa di una nuova osservazione di source e destination.");
        }

        var sourceQuantity =
            inventoryIndex.GetQuantity(
                action.BaseItemId,
                action.IsHq,
                action.Source);

        var destinationQuantity =
            inventoryIndex.GetQuantity(
                action.BaseItemId,
                action.IsHq,
                action.Destination);

        var expectedSourceMaximum =
            Math.Max(
                0,
                baseline.SourceQuantity - action.Quantity);

        var expectedDestinationMinimum =
            baseline.DestinationQuantity + action.Quantity;

        if (sourceQuantity <= expectedSourceMaximum &&
            destinationQuantity >= expectedDestinationMinimum)
        {
            return new PlanExecutionVerificationResult(
                PlanExecutionVerificationStatus.Verified,
                "Delta source/destination osservato.");
        }

        return new PlanExecutionVerificationResult(
            PlanExecutionVerificationStatus.Mismatch,
            $"Delta non coerente. Source {baseline.SourceQuantity}->{sourceQuantity}, " +
            $"Destination {baseline.DestinationQuantity}->{destinationQuantity}.");
    }

    private static bool IsNewerObservation(
        DateTime? current,
        DateTime? baseline)
    {
        if (!current.HasValue)
            return false;

        if (!baseline.HasValue)
            return true;

        return current.Value > baseline.Value;
    }
}
