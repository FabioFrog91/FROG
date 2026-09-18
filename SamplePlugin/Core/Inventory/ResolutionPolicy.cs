using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

public sealed class ResolutionPolicy
{
    private readonly List<InventorySource> sources = new();

    public IReadOnlyList<InventorySource> Sources => sources;

    public ResolutionPolicy(
        IEnumerable<InventorySource> sources,
        ulong mainCharacterId)
    {
        foreach (var source in sources
                     .OrderBy(source =>
                         GetPriority(
                             source,
                             mainCharacterId))
                     .ThenBy(source => source.OwnerId)
                     .ThenBy(source => source.Container))
        {
            Add(source);
        }
    }

    public void Add(InventorySource source)
    {
        if (sources.Contains(source))
            return;

        sources.Add(source);
    }

    public void Clear()
    {
        sources.Clear();
    }

    private static int GetPriority(
        InventorySource source,
        ulong mainCharacterId)
    {
        return source.Storage switch
        {
            StorageType.CharacterInventory =>
                source.OwnerId == mainCharacterId
                    ? 0
                    : 3,

            StorageType.Retainer =>
                source.ParentCharacterId == mainCharacterId
                    ? 1
                    : 4,

            StorageType.FreeCompanyChest =>
                2,

            _ =>
                5
        };
    }
}
