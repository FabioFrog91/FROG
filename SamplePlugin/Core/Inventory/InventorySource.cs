namespace FROG.Core.Inventory;

public sealed record InventorySource(
    StorageType Storage,
    ulong OwnerId,
    uint Container
);
