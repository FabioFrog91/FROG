using System;
using System.Collections.Generic;

namespace FROG.Core.Inventory;

public sealed class ResolutionPolicy
{
    private readonly List<InventorySource> sources = new();

    public IReadOnlyList<InventorySource> Sources => sources;

    public ResolutionPolicy(IEnumerable<InventorySource> sources)
    {
        foreach (var source in sources)
            Add(source);
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
}
