using System.Collections.Generic;

namespace FROG.Core.Inventory;

public sealed class RequirementSet
{
    private readonly List<Requirement> requirements = new();

    public string Name { get; }

    public IReadOnlyList<Requirement> Requirements => requirements;

    public RequirementSet(string name)
    {
        Name = name;
    }

    public void Add(Requirement requirement)
    {
        requirements.Add(requirement);
    }

    public void Clear()
    {
        requirements.Clear();
    }
}
