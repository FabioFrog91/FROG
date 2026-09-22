using System;
using System.Collections.Generic;

namespace FROG.Core.Inventory.Providers;

public interface StorageReaderAPI
{
    bool TryReadActiveRetainer(
        DateTime observedAtUtc,
        out IReadOnlyList<InventorySource> sources,
        out IReadOnlyList<InventoryItemSnapshot> snapshots);

    bool TryReadObservedFreeCompanyPage(
        DateTime observedAtUtc,
        uint container,
        out InventorySource source,
        out IReadOnlyList<InventoryItemSnapshot> snapshots);
}
