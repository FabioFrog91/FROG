namespace FROG.Core.Inventory;

public enum RequirementQualityPolicy
{
    Any,
    HqFirst,
    NqFirst,
    HqOnly,
    NqOnly
}

public sealed record Requirement(
    ulong ItemId,
    int Quantity,
    RequirementQualityPolicy QualityPolicy
);
