using System.Collections.Generic;

namespace FROG.Core.Inventory.Providers;

public interface IStorageSourceProvider
{
    IReadOnlyList<InventorySource> GetSourcesForCharacter(
        ulong characterId);
}
