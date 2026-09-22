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
    uint BaseItemId,
    int Quantity,
    RequirementQualityPolicy QualityPolicy,
    bool IsPrecraft,
    bool IsUntradable = false
);
