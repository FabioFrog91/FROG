using Dalamud.Game.Inventory;

namespace FROG.Core.Inventory;

public static class ExecutionInventoryRules
{
    public static bool IsExecutableContainer(
        InventoryItemSnapshot item) =>
        item.Storage switch
        {
            StorageType.CharacterInventory =>
                item.Container is
                    (uint)GameInventoryType.Inventory1 or
                    (uint)GameInventoryType.Inventory2 or
                    (uint)GameInventoryType.Inventory3 or
                    (uint)GameInventoryType.Inventory4,

            StorageType.Retainer =>
                item.Container >=
                    (uint)GameInventoryType.RetainerPage1 &&
                item.Container <=
                    (uint)GameInventoryType.RetainerPage7,

            StorageType.FreeCompanyChest =>
                item.Container >=
                    (uint)GameInventoryType.FreeCompanyPage1 &&
                item.Container <=
                    (uint)GameInventoryType.FreeCompanyPage5,

            _ => false
        };
}
