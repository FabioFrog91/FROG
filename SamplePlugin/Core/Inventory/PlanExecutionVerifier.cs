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
            GetDestinationQuantity(
                inventoryIndex,
                action),
            inventoryIndex.GetSourceObservedAtUtc(
                action.Source),
            GetDestinationObservedAtUtc(
                inventoryIndex,
                action));
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
            GetDestinationObservedAtUtc(
                inventoryIndex,
                action);

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
            GetDestinationQuantity(
                inventoryIndex,
                action);

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

    private static int GetDestinationQuantity(
        InventoryIndex inventoryIndex,
        PlannerAction action)
    {
        if (action.Destination is null)
            return 0;

        if (action.Destination.Storage ==
            StorageType.CharacterInventory)
        {
            return inventoryIndex.GetCharacterInventoryQuantity(
                action.Destination.OwnerId,
                action.BaseItemId,
                action.IsHq);
        }

        return inventoryIndex.GetQuantity(
            action.BaseItemId,
            action.IsHq,
            action.Destination);
    }

    private static DateTime? GetDestinationObservedAtUtc(
        InventoryIndex inventoryIndex,
        PlannerAction action)
    {
        if (action.Destination is null)
            return null;

        if (action.Destination.Storage ==
            StorageType.CharacterInventory)
        {
            return inventoryIndex.GetCharacterInventoryObservedAtUtc(
                action.Destination.OwnerId);
        }

        return inventoryIndex.GetSourceObservedAtUtc(
            action.Destination);
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
