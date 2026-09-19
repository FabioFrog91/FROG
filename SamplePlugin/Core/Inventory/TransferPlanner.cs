using System.Collections.Generic;

namespace FROG.Core.Inventory;

public sealed class TransferPlanner
{
    private readonly RequirementResolver resolver;

    public TransferPlanner(
        RequirementResolver resolver)
    {
        this.resolver = resolver;
    }

    public TransferPlan Plan(
        RequirementSet requirements,
        InventoryIndex inventoryIndex,
        InventorySourceCatalog sourceCatalog,
        ResolutionPolicy resolutionPolicy)
    {
        var plan = new TransferPlan();

        foreach (var requirement in requirements.Requirements)
        {
            var resolution =
                resolver.Resolve(
                    requirement,
                    inventoryIndex,
                    sourceCatalog,
                    resolutionPolicy);

            plan.Add(resolution);
        }

        return plan;
    }
}
