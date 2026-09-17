using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

public sealed class InventoryIndex
{
    private readonly List<InventoryItemSnapshot> items = new();

    public IReadOnlyList<InventoryItemSnapshot> Items => items;

    public void ReplaceAll(IEnumerable<InventoryItemSnapshot> snapshots)
    {
        items.Clear();
        items.AddRange(snapshots);
    }

    public IEnumerable<InventoryItemSnapshot> Find(ulong itemId)
    {
        return items.Where(x => x.ItemId == itemId);
    }

    public int GetTotalQuantity(ulong itemId)
    {
        return items
            .Where(x => x.ItemId == itemId)
            .Sum(x => x.Quantity);
    }
}
