using System;

namespace FROG.Core.Inventory;

public enum PlanExecutionVerificationStatus
{
    WaitingForExecution,
    WaitingForObservation,
    Verified,
    Mismatch
}

public sealed record PlanExecutionObservation(
    int SourceQuantity,
    int LogicalSourceQuantity,
    int DestinationQuantity,
    DateTime? SourceObservedAtUtc,
    DateTime? DestinationObservedAtUtc,
    long? SourceObservationRevision,
    long? DestinationObservationRevision,
    ulong SourceLayoutFingerprint);

public sealed record PlanExecutionBaseline(
    int ActionIndex,
    int SourceQuantity,
    int LogicalSourceQuantity,
    int DestinationQuantity,
    DateTime? SourceObservedAtUtc,
    DateTime? DestinationObservedAtUtc,
    long? SourceObservationRevision,
    long? DestinationObservationRevision,
    ulong SourceLayoutFingerprint);

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
        var observation =
            Observe(
                action,
                inventoryIndex);

        return new PlanExecutionBaseline(
            actionIndex,
            observation.SourceQuantity,
            observation.LogicalSourceQuantity,
            observation.DestinationQuantity,
            observation.SourceObservedAtUtc,
            observation.DestinationObservedAtUtc,
            observation.SourceObservationRevision,
            observation.DestinationObservationRevision,
            observation.SourceLayoutFingerprint);
    }

    public PlanExecutionObservation Observe(
        PlannerAction action,
        InventoryIndex inventoryIndex)
    {
        if (action.Type == PlannerActionType.SwitchCharacter ||
            action.Source is null ||
            action.Destination is null)
        {
            return new PlanExecutionObservation(
                0,
                0,
                0,
                null,
                null,
                null,
                null,
                0);
        }

        var sourceObservation =
            inventoryIndex.ObserveItem(
                action.BaseItemId,
                action.IsHq,
                action.Source);

        return new PlanExecutionObservation(
            sourceObservation.Quantity,
            sourceObservation.LogicalQuantity,
            GetDestinationQuantity(
                inventoryIndex,
                action),
            sourceObservation.ObservedAtUtc,
            GetDestinationObservedAtUtc(
                inventoryIndex,
                action),
            sourceObservation.ObservationRevision,
            GetDestinationObservationRevision(
                inventoryIndex,
                action),
            sourceObservation.LayoutFingerprint);
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

        var observation =
            Observe(
                action,
                inventoryIndex);

        if (!IsNewerObservation(
                observation.SourceObservationRevision,
                baseline.SourceObservationRevision) ||
            !IsNewerObservation(
                observation.DestinationObservationRevision,
                baseline.DestinationObservationRevision))
        {
            return new PlanExecutionVerificationResult(
                PlanExecutionVerificationStatus.WaitingForObservation,
                "In attesa di una nuova osservazione di source e destination.");
        }

        var expectedSourceMaximum =
            Math.Max(
                0,
                baseline.SourceQuantity - action.Quantity);

        var expectedDestinationMinimum =
            baseline.DestinationQuantity + action.Quantity;

        if (observation.SourceQuantity <= expectedSourceMaximum &&
            observation.DestinationQuantity >= expectedDestinationMinimum)
        {
            return new PlanExecutionVerificationResult(
                PlanExecutionVerificationStatus.Verified,
                "Delta source/destination osservato.");
        }

        return new PlanExecutionVerificationResult(
            PlanExecutionVerificationStatus.Mismatch,
            $"Delta non coerente. Source {baseline.SourceQuantity}->{observation.SourceQuantity}, " +
            $"Destination {baseline.DestinationQuantity}->{observation.DestinationQuantity}.");
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

        if (action.Destination.Storage ==
            StorageType.FreeCompanyChest)
        {
            return inventoryIndex.GetFreeCompanyQuantity(
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

        if (action.Destination.Storage ==
            StorageType.FreeCompanyChest)
        {
            return inventoryIndex.GetFreeCompanyObservedAtUtc(
                action.Destination.OwnerId);
        }

        return inventoryIndex.GetSourceObservedAtUtc(
            action.Destination);
    }

    private static long? GetDestinationObservationRevision(
        InventoryIndex inventoryIndex,
        PlannerAction action)
    {
        if (action.Destination is null)
            return null;

        if (action.Destination.Storage ==
            StorageType.CharacterInventory)
        {
            return inventoryIndex.GetCharacterInventoryObservationRevision(
                action.Destination.OwnerId);
        }

        if (action.Destination.Storage ==
            StorageType.FreeCompanyChest)
        {
            return inventoryIndex.GetFreeCompanyObservationRevision(
                action.Destination.OwnerId);
        }

        return inventoryIndex.GetSourceObservationRevision(
            action.Destination);
    }

    private static bool IsNewerObservation(
        long? current,
        long? baseline)
    {
        if (!current.HasValue)
            return false;

        if (!baseline.HasValue)
            return true;

        return current.Value > baseline.Value;
    }
}
