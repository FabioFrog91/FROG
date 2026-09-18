using System.Collections.Generic;

namespace FROG.Core.Inventory;

public sealed class RequirementSet
{
    private readonly List<Requirement> requirements = new();

    public IReadOnlyList<Requirement> Requirements => requirements;

    public void Add(Requirement requirement)
    {
        requirements.Add(requirement);
    }

    public void Clear()
    {
        requirements.Clear();
    }
}
