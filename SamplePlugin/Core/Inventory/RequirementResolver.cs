using System;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

public sealed class RequirementResolver
{
    public RequirementResolution Resolve(
        Requirement requirement,
        InventoryIndex inventoryIndex,
        InventorySourceCatalog sourceCatalog)
    {
        var usableSources = resolutionPolicy.Sources
            .Where(source =>
                sourceCatalog.Sources.Any(policy =>
                    policy.Source == source &&
                    policy.Read &&
                    policy.Use))
            .ToList();

        if (requirement.Quantity <= 0)
        {
            return new RequirementResolution(
                requirement,
                0,
                0,
                Array.Empty<RequirementAllocation>());
        }

        var allocations = requirement.QualityPolicy switch
        {
            RequirementQualityPolicy.HqOnly =>
                AllocateQuality(
                    requirement,
                    inventoryIndex,
                    usableSources,
                    isHq: true),

            RequirementQualityPolicy.NqOnly =>
                AllocateQuality(
                    requirement,
                    inventoryIndex,
                    usableSources,
                    isHq: false),

            RequirementQualityPolicy.HqFirst =>
                AllocatePreferredQuality(
                    requirement,
                    inventoryIndex,
                    usableSources,
                    preferredIsHq: true),

            RequirementQualityPolicy.NqFirst =>
                AllocatePreferredQuality(
                    requirement,
                    inventoryIndex,
                    usableSources,
                    preferredIsHq: false),

            RequirementQualityPolicy.Any =>
                AllocateAny(
                    requirement,
                    inventoryIndex,
                    usableSources),

            _ => new List<RequirementAllocation>()
        };

        var available = allocations.Sum(x => x.Quantity);
        var missing = requirement.Quantity - available;

        return new RequirementResolution(
            requirement,
            available,
            missing,
            allocations);
    }

    private static List<RequirementAllocation> AllocateQuality(
        Requirement requirement,
        InventoryIndex inventoryIndex,
        IReadOnlyList<InventorySource> sources,
        bool isHq)
    {
        var allocations = new List<RequirementAllocation>();
        var remaining = requirement.Quantity;

        foreach (var sourcePolicy in sources)
        {
            var quantity = isHq
                ? inventoryIndex.GetHqQuantity(
                    requirement.BaseItemId,
                    source)
                : inventoryIndex.GetNqQuantity(
                    requirement.BaseItemId,
                    sourcePolicy.Source);

            var used = Math.Min(quantity, remaining);

            if (used > 0)
            {
                allocations.Add(
                    new RequirementAllocation(
                        requirement.BaseItemId,
                        sourcePolicy.Source,
                        used,
                        isHq));
            }

            remaining -= used;

            if (remaining == 0)
                break;
        }

        return allocations;
    }

    private static List<RequirementAllocation> AllocatePreferredQuality(
        Requirement requirement,
        InventoryIndex inventoryIndex,
        IReadOnlyList<SourcePolicy> sources,
        bool preferredIsHq)
    {
        var allocations = AllocateQuality(
            requirement,
            inventoryIndex,
            sources,
            preferredIsHq);

        var allocated = allocations.Sum(x => x.Quantity);
        var remaining = requirement.Quantity - allocated;

        if (remaining <= 0)
            return allocations;

        allocations.AddRange(
            AllocateRemainingQuality(
                requirement,
                inventoryIndex,
                sources,
                !preferredIsHq,
                remaining));

        return allocations;
    }

    private static List<RequirementAllocation> AllocateRemainingQuality(
        Requirement requirement,
        InventoryIndex inventoryIndex,
        IReadOnlyList<SourcePolicy> sources,
        bool isHq,
        int quantityNeeded)
    {
        var allocations = new List<RequirementAllocation>();
        var remaining = quantityNeeded;

        foreach (var sourcePolicy in sources)
        {
            var quantity = isHq
                ? inventoryIndex.GetHqQuantity(
                    requirement.BaseItemId,
                    sourcePolicy.Source)
                : inventoryIndex.GetNqQuantity(
                    requirement.BaseItemId,
                    sourcePolicy.Source);

            var used = Math.Min(quantity, remaining);

            if (used > 0)
            {
                allocations.Add(
                    new RequirementAllocation(
                        requirement.BaseItemId,
                        sourcePolicy.Source,
                        used,
                        isHq));
            }

            remaining -= used;

            if (remaining == 0)
                break;
        }

        return allocations;
    }

    private static List<RequirementAllocation> AllocateAny(
        Requirement requirement,
        InventoryIndex inventoryIndex,
        IReadOnlyList<SourcePolicy> sources)
    {
        var allocations = new List<RequirementAllocation>();
        var remaining = requirement.Quantity;

        foreach (var sourcePolicy in sources)
        {
            var items = inventoryIndex
                .Find(requirement.BaseItemId)
                .Where(x =>
                    x.Storage == sourcePolicy.Source.Storage &&
                    x.OwnerId == sourcePolicy.Source.OwnerId &&
                    x.Container == sourcePolicy.Source.Container)
                .OrderBy(x => x.IsHq)
                .ThenBy(x => x.Slot)
                .ToList();

            foreach (var item in items)
            {
                var used = Math.Min(
                    item.Quantity,
                    remaining);

                if (used > 0)
                {
                    allocations.Add(
                        new RequirementAllocation(
                            requirement.BaseItemId,
                            sourcePolicy.Source,
                            used,
                            item.IsHq));
                }

                remaining -= used;

                if (remaining == 0)
                    return allocations;
            }
        }

        return allocations;
    }
}
