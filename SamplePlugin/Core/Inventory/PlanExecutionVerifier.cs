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
    int DecisionIndex,
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
        PlannerDecision decision,
        InventoryIndex inventoryIndex)
    {
        var observation =
            Observe(
                decision,
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
        PlannerDecision decision,
        InventoryIndex inventoryIndex)
    {
        if (decision.Type ==
                PlannerDecisionType.SwitchCharacter ||
            decision.Source is null ||
            decision.Destination is null)
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
            inventoryIndex.ObserveLogicalItem(
                decision.BaseItemId,
                decision.IsHq,
                decision.Source);

        return new PlanExecutionObservation(
            sourceObservation.Quantity,
            sourceObservation.LogicalQuantity,
            GetDestinationQuantity(
                inventoryIndex,
                decision),
            sourceObservation.ObservedAtUtc,
            GetDestinationObservedAtUtc(
                inventoryIndex,
                decision),
            sourceObservation.ObservationRevision,
            GetDestinationObservationRevision(
                inventoryIndex,
                decision),
            sourceObservation.LayoutFingerprint);
    }

    public PlanExecutionVerificationResult Verify(
        PlannerDecision decision,
        PlanExecutionBaseline baseline,
        InventoryIndex inventoryIndex,
        ulong currentCharacterId) =>
        Verify(
            decision,
            baseline,
            Observe(
                decision,
                inventoryIndex),
            currentCharacterId);

    public PlanExecutionVerificationResult Verify(
        PlannerDecision decision,
        PlanExecutionBaseline baseline,
        PlanExecutionObservation observation,
        ulong currentCharacterId)
    {
        if (decision.Type ==
            PlannerDecisionType.SwitchCharacter)
        {
            return currentCharacterId ==
                   decision.ToCharacterId
                ? new PlanExecutionVerificationResult(
                    PlanExecutionVerificationStatus.Verified,
                    "Cambio personaggio osservato.")
                : new PlanExecutionVerificationResult(
                    PlanExecutionVerificationStatus.WaitingForObservation,
                    "In attesa del personaggio previsto.");
        }

        if (decision.Source is null ||
            decision.Destination is null)
        {
            return new PlanExecutionVerificationResult(
                PlanExecutionVerificationStatus.Mismatch,
                "MOVE privo di source o destination.");
        }

        var diagnostics =
            FormatMoveDiagnostics(
                decision,
                baseline,
                observation);

        if (!IsNewerObservation(
                observation.SourceObservationRevision,
                baseline.SourceObservationRevision) ||
            !IsNewerObservation(
                observation.DestinationObservationRevision,
                baseline.DestinationObservationRevision))
        {
            return new PlanExecutionVerificationResult(
                PlanExecutionVerificationStatus.WaitingForObservation,
                $"In attesa di una nuova osservazione di source e destination. {diagnostics}");
        }

        var sourceDecrease =
            baseline.SourceQuantity -
            observation.SourceQuantity;

        var destinationIncrease =
            observation.DestinationQuantity -
            baseline.DestinationQuantity;

        if (sourceDecrease == 0 &&
            destinationIncrease == 0)
        {
            return new PlanExecutionVerificationResult(
                PlanExecutionVerificationStatus.WaitingForObservation,
                $"Nuove osservazioni ricevute, ma nessun delta del MOVE è stato ancora osservato. {diagnostics}");
        }

        if (sourceDecrease < 0 ||
            destinationIncrease < 0 ||
            sourceDecrease !=
                destinationIncrease)
        {
            return new PlanExecutionVerificationResult(
                PlanExecutionVerificationStatus.Mismatch,
                $"Delta non coerente. {diagnostics}");
        }

        if (sourceDecrease >=
            decision.Quantity)
        {
            return new PlanExecutionVerificationResult(
                PlanExecutionVerificationStatus.Verified,
                sourceDecrease > decision.Quantity
                    ? $"MOVE soddisfatto con quantità aggiuntiva: osservate {sourceDecrease}/{decision.Quantity}. {diagnostics}"
                    : $"Delta source/destination completo osservato. {diagnostics}");
        }

        return new PlanExecutionVerificationResult(
            PlanExecutionVerificationStatus.WaitingForObservation,
            $"Trasferimento parziale coerente: {sourceDecrease}/{decision.Quantity}. {diagnostics}");
    }

    private static string FormatMoveDiagnostics(
        PlannerDecision decision,
        PlanExecutionBaseline baseline,
        PlanExecutionObservation observation)
    {
        var sourceDecrease =
            baseline.SourceQuantity -
            observation.SourceQuantity;

        var destinationIncrease =
            observation.DestinationQuantity -
            baseline.DestinationQuantity;

        return
            $"Planned={decision.Quantity}; " +
            $"Source={baseline.SourceQuantity}->{observation.SourceQuantity} (delta={sourceDecrease}); " +
            $"LogicalSource={baseline.LogicalSourceQuantity}->{observation.LogicalSourceQuantity}; " +
            $"Destination={baseline.DestinationQuantity}->{observation.DestinationQuantity} (delta={destinationIncrease}); " +
            $"SourceRev={FormatRevision(baseline.SourceObservationRevision)}->{FormatRevision(observation.SourceObservationRevision)}; " +
            $"DestinationRev={FormatRevision(baseline.DestinationObservationRevision)}->{FormatRevision(observation.DestinationObservationRevision)}; " +
            $"Layout={baseline.SourceLayoutFingerprint}->{observation.SourceLayoutFingerprint}.";
    }

    private static string FormatRevision(
        long? revision) =>
        revision?.ToString() ?? "null";

    private static int GetDestinationQuantity(
        InventoryIndex inventoryIndex,
        PlannerDecision decision)
    {
        if (decision.Destination is null)
            return 0;

        if (decision.Destination.Storage ==
            StorageType.CharacterInventory)
        {
            return inventoryIndex.GetCharacterInventoryQuantity(
                decision.Destination.OwnerId,
                decision.BaseItemId,
                decision.IsHq);
        }

        if (decision.Destination.Storage ==
            StorageType.FreeCompanyChest)
        {
            return inventoryIndex.GetFreeCompanyQuantity(
                decision.Destination.OwnerId,
                decision.BaseItemId,
                decision.IsHq);
        }

        return inventoryIndex.ObserveLogicalItem(
                decision.BaseItemId,
                decision.IsHq,
                decision.Destination)
            .Quantity;
    }

    private static DateTime? GetDestinationObservedAtUtc(
        InventoryIndex inventoryIndex,
        PlannerDecision decision)
    {
        if (decision.Destination is null)
            return null;

        if (decision.Destination.Storage ==
            StorageType.CharacterInventory)
        {
            return inventoryIndex.GetCharacterInventoryObservedAtUtc(
                decision.Destination.OwnerId);
        }

        if (decision.Destination.Storage ==
            StorageType.FreeCompanyChest)
        {
            return inventoryIndex.GetFreeCompanyObservedAtUtc(
                decision.Destination.OwnerId);
        }

        return inventoryIndex.ObserveLogicalItem(
                decision.BaseItemId,
                decision.IsHq,
                decision.Destination)
            .ObservedAtUtc;
    }

    private static long? GetDestinationObservationRevision(
        InventoryIndex inventoryIndex,
        PlannerDecision decision)
    {
        if (decision.Destination is null)
            return null;

        if (decision.Destination.Storage ==
            StorageType.CharacterInventory)
        {
            return inventoryIndex.GetCharacterInventoryObservationRevision(
                decision.Destination.OwnerId);
        }

        if (decision.Destination.Storage ==
            StorageType.FreeCompanyChest)
        {
            return inventoryIndex.GetFreeCompanyObservationRevision(
                decision.Destination.OwnerId);
        }

        return inventoryIndex.ObserveLogicalItem(
                decision.BaseItemId,
                decision.IsHq,
                decision.Destination)
            .ObservationRevision;
    }

    private static bool IsNewerObservation(
        long? current,
        long? baseline)
    {
        if (!current.HasValue)
            return false;

        if (!baseline.HasValue)
            return true;

        return current.Value >
            baseline.Value;
    }
}
