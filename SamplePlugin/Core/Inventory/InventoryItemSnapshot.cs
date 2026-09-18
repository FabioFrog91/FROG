using System;

namespace FROG.Core.Inventory;

public sealed record InventoryItemSnapshot(
    uint BaseItemId,
    uint RawItemId,
    int Quantity,
    bool IsHq,
    StorageType Storage,
    ulong OwnerId,
    uint Container,
    int Slot,
    DateTime ObservedAtUtc,
    bool IsVerified
);
