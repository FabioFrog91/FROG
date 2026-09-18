namespace FROG.Core.Inventory;

public sealed record InventoryStackAllocation(
    InventoryItemSnapshot Item,
    int Quantity
);
