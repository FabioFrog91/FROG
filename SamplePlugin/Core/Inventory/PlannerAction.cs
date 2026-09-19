using FROG.Core.Inventory;

namespace FROG.Core.Inventory;

public enum PlannerActionType
{
    Move,
    SwitchCharacter
}

public sealed record PlannerAction(
    PlannerActionType Type,
    InventorySource? Source,
    InventorySource? Destination,
    uint BaseItemId,
    bool IsHq,
    int Quantity,
    ulong FromCharacterId,
    ulong ToCharacterId)
{
    public static PlannerAction Move(
        InventorySource source,
        InventorySource destination,
        uint baseItemId,
        bool isHq,
        int quantity) =>
        new(
            PlannerActionType.Move,
            source,
            destination,
            baseItemId,
            isHq,
            quantity,
            0,
            0);

    public static PlannerAction SwitchCharacter(
        ulong fromCharacterId,
        ulong toCharacterId) =>
        new(
            PlannerActionType.SwitchCharacter,
            null,
            null,
            0,
            false,
            0,
            fromCharacterId,
            toCharacterId);
}
