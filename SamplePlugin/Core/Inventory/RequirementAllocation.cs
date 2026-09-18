namespace FROG.Core.Inventory;

public sealed record RequirementAllocation(
    ulong ItemId,
    InventorySource Source,
    int Quantity,
    bool IsHq
);
