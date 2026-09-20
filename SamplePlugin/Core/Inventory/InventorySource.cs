namespace FROG.Core.Inventory;

public sealed record InventorySource(
    StorageType Storage,
    ulong OwnerId,
    uint Container,
    ulong ParentCharacterId = 0,
    string OwnerName = ""
)
{
    public const uint AnyFreeCompanyPageContainer = 0;
}
