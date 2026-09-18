using System;
using System.Collections.Generic;

namespace FROG.Core.Inventory.Providers;

public interface ICharacterInventoryProvider
{
    bool TryGetCurrentCharacter(
        out ulong characterId);

    IReadOnlyList<InventoryItemSnapshot> ReadCurrentInventory(
        ulong characterId,
        DateTime observedAtUtc);
}
