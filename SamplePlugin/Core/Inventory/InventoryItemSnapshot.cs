namespace FROG.Core.Inventory;

public sealed record InventoryItemSnapshot(
    ulong ItemId,
    int Quantity,
    bool IsHq,
    StorageType Storage,
    ulong OwnerId,
    uint Container,
    int Slot
);
