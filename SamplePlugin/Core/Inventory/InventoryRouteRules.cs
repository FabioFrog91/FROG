namespace FROG.Core.Inventory;

public static class InventoryRouteRules
{
    public static bool IsCharacterAccessible(
        ulong currentCharacterId,
        InventorySource source) =>
        IsCharacterAccessible(
            currentCharacterId,
            source.Storage,
            source.OwnerId,
            source.ParentCharacterId);

    public static bool IsCharacterAccessible(
        ulong currentCharacterId,
        PlannerLogicalSource source) =>
        IsCharacterAccessible(
            currentCharacterId,
            source.Storage,
            source.OwnerId,
            source.ParentCharacterId);

    public static bool IsLegalRoute(
        InventorySource source,
        InventorySource destination) =>
        IsLegalRoute(
            source.Storage,
            source.OwnerId,
            source.ParentCharacterId,
            destination.Storage,
            destination.OwnerId,
            destination.ParentCharacterId);

    public static bool IsLegalRoute(
        PlannerLogicalSource source,
        PlannerLogicalSource destination) =>
        IsLegalRoute(
            source.Storage,
            source.OwnerId,
            source.ParentCharacterId,
            destination.Storage,
            destination.OwnerId,
            destination.ParentCharacterId);

    private static bool IsCharacterAccessible(
        ulong currentCharacterId,
        StorageType storage,
        ulong ownerId,
        ulong parentCharacterId) =>
        storage switch
        {
            StorageType.CharacterInventory =>
                ownerId ==
                currentCharacterId,

            StorageType.Retainer =>
                parentCharacterId ==
                currentCharacterId,

            StorageType.FreeCompanyChest =>
                true,

            _ => false
        };

    private static bool IsLegalRoute(
        StorageType sourceStorage,
        ulong sourceOwnerId,
        ulong sourceParentCharacterId,
        StorageType destinationStorage,
        ulong destinationOwnerId,
        ulong destinationParentCharacterId)
    {
        if (sourceStorage ==
            StorageType.Retainer)
        {
            return destinationStorage ==
                       StorageType.CharacterInventory &&
                   destinationOwnerId ==
                       sourceParentCharacterId;
        }

        if (sourceStorage ==
            StorageType.CharacterInventory)
        {
            return destinationStorage ==
                       StorageType.FreeCompanyChest &&
                   (destinationParentCharacterId == 0 ||
                    destinationParentCharacterId ==
                        sourceOwnerId);
        }

        if (sourceStorage ==
            StorageType.FreeCompanyChest)
        {
            return destinationStorage ==
                StorageType.CharacterInventory;
        }

        return false;
    }
}
