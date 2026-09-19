namespace FROG.Core.Inventory;

public sealed record TransferIntent(
    uint BaseItemId,
    InventorySource Source,
    int Quantity,
    bool IsHq
);
