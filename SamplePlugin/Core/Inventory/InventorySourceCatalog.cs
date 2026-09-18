using System.Collections.Generic;

namespace FROG.Core.Inventory;

public sealed class InventorySourceCatalog
{
    private readonly List<SourcePolicy> sources = new();

    public IReadOnlyList<SourcePolicy> Sources => sources;

    public void Add(SourcePolicy source)
    {
        sources.Add(source);
    }

    public void Clear()
    {
        sources.Clear();
    }
}
