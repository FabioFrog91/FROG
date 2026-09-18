namespace FROG.Core.Inventory;

public sealed record SourcePolicy(
    InventorySource Source,
    bool Read,
    bool Use,
    int Priority
);
