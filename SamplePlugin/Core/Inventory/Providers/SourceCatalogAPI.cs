using System.Collections.Generic;

namespace FROG.Core.Inventory.Providers;

public interface SourceCatalogAPI
{
    IReadOnlyList<InventorySource> GetSourcesForCharacter(
        ulong characterId);
}
