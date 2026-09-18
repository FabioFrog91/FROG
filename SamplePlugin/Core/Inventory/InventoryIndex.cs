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

    public IEnumerable<InventoryItemSnapshot> FindNq(ulong itemId)
    {
        return items.Where(x => x.ItemId == itemId && !x.IsHq);
    }

    public IEnumerable<InventoryItemSnapshot> FindHq(ulong itemId)
    {
        return items.Where(x => x.ItemId == itemId && x.IsHq);
    }

    public int GetTotalQuantity(ulong itemId)
    {
        return items
            .Where(x => x.ItemId == itemId)
            .Sum(x => x.Quantity);
    }

    public int GetNqQuantity(ulong itemId)
    {
        return items
            .Where(x => x.ItemId == itemId && !x.IsHq)
            .Sum(x => x.Quantity);
    }

    public int GetHqQuantity(ulong itemId)
    {
        return items
            .Where(x => x.ItemId == itemId && x.IsHq)
            .Sum(x => x.Quantity);
    }

    public int GetTotalQuantity(
    ulong itemId,
    InventorySource source)
    {
        return items
            .Where(x =>
                x.ItemId == itemId &&
                x.Storage == source.Storage &&
                x.OwnerId == source.OwnerId &&
                x.Container == source.Container)
            .Sum(x => x.Quantity);
    }

    public int GetNqQuantity(
        ulong itemId,
        InventorySource source)
    {
        return items
            .Where(x =>
                x.ItemId == itemId &&
                !x.IsHq &&
                x.Storage == source.Storage &&
                x.OwnerId == source.OwnerId &&
                x.Container == source.Container)
            .Sum(x => x.Quantity);
    }

    public int GetHqQuantity(
        ulong itemId,
        InventorySource source)
    {
        return items
            .Where(x =>
                x.ItemId == itemId &&
                x.IsHq &&
                x.Storage == source.Storage &&
                x.OwnerId == source.OwnerId &&
                x.Container == source.Container)
            .Sum(x => x.Quantity);
    }

    public IEnumerable<InventoryItemSnapshot> Find(
    InventorySource source,
    bool isHq)
    {
        return items.Where(x =>
            x.ItemId != 0 &&
            x.IsHq == isHq &&
            x.Storage == source.Storage &&
            x.OwnerId == source.OwnerId &&
            x.Container == source.Container);
    }



}
