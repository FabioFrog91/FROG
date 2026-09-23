using System;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

public sealed class InventoryStackResolver
{
    public IReadOnlyList<InventoryStackAllocation> Resolve(
        RequirementAllocation allocation,
        InventoryIndex inventoryIndex)
    {
        var remaining = allocation.Quantity;

        var matchingItems = inventoryIndex
            .Find(allocation.Source, allocation.IsHq)
            .Where(x => x.BaseItemId == allocation.BaseItemId)
            .OrderBy(x => x.Container)
            .ThenBy(x => x.Slot)
            .ToList();

        var result = new List<InventoryStackAllocation>();

        foreach (var item in matchingItems)
        {
            if (remaining <= 0)
                break;

            var quantity = Math.Min(
                item.Quantity,
                remaining);

            result.Add(
                new InventoryStackAllocation(
                    item,
                    quantity));

            remaining -= quantity;
        }

        return result;
    }
}
