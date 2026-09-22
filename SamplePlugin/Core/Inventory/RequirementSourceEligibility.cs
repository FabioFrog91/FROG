namespace FROG.Core.Inventory;

/// <summary>
/// Centralizes which physical sources may satisfy a requirement.
/// Untradable items can only be used when they already belong to the main
/// character or one of that character's retainers.
/// </summary>
public static class RequirementSourceEligibility
{
    public static bool CanUse(
        Requirement requirement,
        InventorySource source,
        ulong mainCharacterId)
    {
        if (!requirement.IsUntradable)
            return true;

        return source.Storage switch
        {
            StorageType.CharacterInventory =>
                source.OwnerId == mainCharacterId,

            StorageType.Retainer =>
                source.ParentCharacterId == mainCharacterId,

            _ =>
                false
        };
    }
}
