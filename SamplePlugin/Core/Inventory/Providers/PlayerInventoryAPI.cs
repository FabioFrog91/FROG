using System;
using System.Collections.Generic;

namespace FROG.Core.Inventory.Providers;

public interface PlayerInventoryAPI
{
    bool TryGetCurrentCharacter(
        out ulong characterId);

    IReadOnlyList<InventoryItemSnapshot> ReadCurrentInventory(
        ulong characterId,
        DateTime observedAtUtc);
}
