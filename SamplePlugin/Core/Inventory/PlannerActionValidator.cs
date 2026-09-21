namespace FROG.Core.Inventory;

public sealed class PlannerActionValidator
{
    public bool CanApply(
        PlannerState state,
        PlannerAction action,
        out string reason)
    {
        reason = string.Empty;

        return action.Type switch
        {
            PlannerActionType.Move =>
                ValidateMove(state, action, out reason),

            PlannerActionType.SwitchCharacter =>
                ValidateCharacterSwitch(state, action, out reason),

            _ => Fail(
                out reason,
                "Unknown planner action.")
        };
    }

    private static bool ValidateMove(
        PlannerState state,
        PlannerAction action,
        out string reason)
    {
        reason = string.Empty;

        if (action.Source is null || action.Destination is null)
            return Fail(out reason, "A move requires both a source and a destination.");

        if (action.Quantity <= 0)
            return Fail(out reason, "Move quantity must be greater than zero.");

        if (action.Source == action.Destination)
            return Fail(out reason, "Source and destination cannot be the same storage.");

        if (!IsCharacterAccessible(state.CurrentCharacterId, action.Source))
            return Fail(out reason, "The source is not accessible from the current character.");

        if (!IsCharacterAccessible(state.CurrentCharacterId, action.Destination))
            return Fail(out reason, "The destination is not accessible from the current character.");

        if (!IsLegalRoute(action.Source, action.Destination))
            return Fail(out reason, "The requested storage-to-storage route is not physically legal.");

        if (state.GetQuantity(
                action.BaseItemId,
                action.IsHq,
                action.Source) < action.Quantity)
        {
            return Fail(out reason, "The source does not contain enough of the requested item.");
        }

        if (state.GetMaximumMovableQuantity(
                action.Source,
                action.Destination,
                action.BaseItemId,
                action.IsHq,
                action.Quantity) < action.Quantity)
        {
            return Fail(
                out reason,
                "The destination does not have enough logical stack capacity.");
        }

        return true;
    }

    private static bool ValidateCharacterSwitch(
        PlannerState state,
        PlannerAction action,
        out string reason)
    {
        reason = string.Empty;

        if (action.FromCharacterId == 0 || action.ToCharacterId == 0)
            return Fail(out reason, "Character switch requires valid character IDs.");

        if (action.FromCharacterId != state.CurrentCharacterId)
            return Fail(out reason, "The switch source does not match the current character.");

        if (action.FromCharacterId == action.ToCharacterId)
            return Fail(out reason, "Cannot switch to the same character.");

        if (action.ToCharacterId != state.MainCharacterId &&
            state.HasVisitedCharacter(action.ToCharacterId))
        {
            return Fail(out reason, "The planner does not revisit a non-main character.");
        }

        return true;
    }

    private static bool IsCharacterAccessible(
        ulong currentCharacterId,
        InventorySource source) =>
        source.Storage switch
        {
            StorageType.CharacterInventory =>
                source.OwnerId == currentCharacterId,

            StorageType.Retainer =>
                source.ParentCharacterId == currentCharacterId,

            StorageType.FreeCompanyChest =>
                true,

            _ => false
        };

    private static bool IsLegalRoute(
        InventorySource source,
        InventorySource destination)
    {
        if (source.Storage == StorageType.Retainer)
        {
            return destination.Storage == StorageType.CharacterInventory &&
                   destination.OwnerId == source.ParentCharacterId;
        }

        if (source.Storage == StorageType.CharacterInventory)
        {
            return destination.Storage == StorageType.FreeCompanyChest &&
                   (destination.ParentCharacterId == 0 ||
                    destination.ParentCharacterId == source.OwnerId);
        }

        if (source.Storage == StorageType.FreeCompanyChest)
        {
            return destination.Storage == StorageType.CharacterInventory;
        }

        return false;
    }

    private static bool Fail(
        out string reason,
        string message)
    {
        reason = message;
        return false;
    }
}
