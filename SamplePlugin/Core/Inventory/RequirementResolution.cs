using System.Collections.Generic;

namespace FROG.Core.Inventory;

public sealed record RequirementResolution(
    Requirement Requirement,
    int Available,
    int Missing,
    IReadOnlyList<RequirementAllocation> Allocations
);
