namespace FROG.Core.Inventory;

public sealed record RequirementAllocation(
    uint BaseItemId,
    InventorySource Source,
    int Quantity,
    bool IsHq
);
