using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

public sealed class TransferPlan
{
    private readonly List<RequirementResolution> resolutions = new();

    public IReadOnlyList<RequirementResolution> Resolutions =>
        resolutions;

    public IReadOnlyList<TransferIntent> Intents =>
        resolutions
            .SelectMany(resolution =>
                resolution.Allocations.Select(allocation =>
                    new TransferIntent(
                        allocation.BaseItemId,
                        allocation.Source,
                        allocation.Quantity,
                        allocation.IsHq)))
            .ToList();

    public IReadOnlyList<RequirementAllocation> Allocations =>
        resolutions
            .SelectMany(x => x.Allocations)
            .ToList();

    public int Missing =>
        resolutions.Sum(x => x.Missing);

    public bool IsComplete =>
        Missing == 0;

    public void Add(RequirementResolution resolution)
    {
        resolutions.Add(resolution);
    }

    public void Clear()
    {
        resolutions.Clear();
    }
}
