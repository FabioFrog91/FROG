using System;
using System.Collections.Generic;

namespace FROG.Core.Inventory.Providers;

public interface StorageReaderAPI
{
    bool TryReadActiveRetainer(
        DateTime observedAtUtc,
        out IReadOnlyList<InventorySource> sources,
        out IReadOnlyList<InventoryItemSnapshot> snapshots);

    bool TryReadActiveFreeCompany(
        DateTime observedAtUtc,
        out IReadOnlyList<InventorySource> sources,
        out IReadOnlyList<InventoryItemSnapshot> snapshots);
}
