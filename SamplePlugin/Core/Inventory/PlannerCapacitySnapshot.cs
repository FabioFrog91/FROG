using Dalamud.Game.Inventory;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

/// <summary>
/// Compact planner-only view of logical storage capacity.
///
/// Unrelated item identities are deliberately discarded: only occupied slot
/// counts and merge space for planner-relevant items are retained. This keeps
/// the background planner independent from live inventory services while
/// still accounting for every occupied slot observed in InventoryIndex.
/// </summary>
public sealed class PlannerCapacitySnapshot
{
    private const int CharacterInventorySlots = 4 * 35;
    private const int RetainerInventorySlots = 7 * 25;
    private const int FreeCompanyChestSlots = 5 * 50;

    private readonly Dictionary<LogicalStorageKey, int> totalSlots;
    private readonly Dictionary<LogicalStorageKey, int> occupiedSlots;
    private readonly Dictionary<LogicalStackKey, int> mergeSpace;
    private readonly Dictionary<uint, int> maximumStacks;

    private PlannerCapacitySnapshot(
        Dictionary<LogicalStorageKey, int> totalSlots,
        Dictionary<LogicalStorageKey, int> occupiedSlots,
        Dictionary<LogicalStackKey, int> mergeSpace,
        Dictionary<uint, int> maximumStacks)
    {
        this.totalSlots = totalSlots;
        this.occupiedSlots = occupiedSlots;
        this.mergeSpace = mergeSpace;
        this.maximumStacks = maximumStacks;
    }

    public static PlannerCapacitySnapshot Capture(
        IReadOnlyList<InventoryItemSnapshot> allItems,
        IReadOnlyList<InventorySource> plannerSources,
        IReadOnlyDictionary<uint, int> maximumStacks)
    {
        var totals =
            plannerSources
                .Select(ToLogicalStorageKey)
                .Distinct()
                .ToDictionary(
                    key => key,
                    key => GetLogicalSlotCount(key.Storage));

        var occupied =
            allItems
                .Where(IsCapacityItem)
                .Select(item =>
                    new
                    {
                        Storage = ToLogicalStorageKey(item),
                        item.Container,
                        item.Slot
                    })
                .Distinct()
                .GroupBy(item => item.Storage)
                .ToDictionary(
                    group => group.Key,
                    group => group.Count());

        var merge =
            new Dictionary<LogicalStackKey, int>();

        foreach (var item in allItems.Where(IsCapacityItem))
        {
            if (!maximumStacks.TryGetValue(
                    item.BaseItemId,
                    out var maximumStack) ||
                maximumStack <= 0)
            {
                continue;
            }

            var available =
                Math.Max(
                    0,
                    maximumStack - item.Quantity);

            if (available == 0)
                continue;

            var key =
                new LogicalStackKey(
                    ToLogicalStorageKey(item),
                    item.BaseItemId,
                    item.IsHq);

            merge[key] =
                merge.GetValueOrDefault(key) +
                available;
        }

        return new PlannerCapacitySnapshot(
            totals,
            occupied,
            merge,
            maximumStacks.ToDictionary(
                entry => entry.Key,
                entry => entry.Value));
    }

    public PlannerCapacitySnapshot Rebase(
        IReadOnlyList<InventoryItemSnapshot> allItems) =>
        Capture(
            allItems,
            totalSlots
                .Select(entry =>
                    new InventorySource(
                        entry.Key.Storage,
                        entry.Key.OwnerId,
                        GetCanonicalContainer(entry.Key.Storage)))
                .ToList(),
            maximumStacks);

    public bool TryGetMaximumStack(
        uint baseItemId,
        out int maximumStack) =>
        maximumStacks.TryGetValue(
            baseItemId,
            out maximumStack) &&
        maximumStack > 0;

    public int GetAcceptableQuantity(
        InventorySource destination,
        uint baseItemId,
        bool isHq,
        int requestedQuantity)
    {
        if (requestedQuantity <= 0 ||
            !TryGetMaximumStack(
                baseItemId,
                out var maximumStack))
        {
            return 0;
        }

        var storageKey =
            ToLogicalStorageKey(destination);

        if (!totalSlots.TryGetValue(
                storageKey,
                out var slotCount) ||
            slotCount <= 0)
        {
            return 0;
        }

        var stackKey =
            new LogicalStackKey(
                storageKey,
                baseItemId,
                isHq);

        var compatibleMergeSpace =
            mergeSpace.GetValueOrDefault(stackKey);

        var emptySlots =
            Math.Max(
                0,
                slotCount -
                occupiedSlots.GetValueOrDefault(storageKey));

        // Exactly one empty slot is kept for the whole logical destination,
        // not one per physical page/container.
        var usableEmptySlots =
            Math.Max(
                0,
                emptySlots - 1);

        var totalCapacity =
            (long)compatibleMergeSpace +
            (long)usableEmptySlots * maximumStack;

        return checked(
            (int)Math.Min(
                requestedQuantity,
                Math.Min(
                    int.MaxValue,
                    totalCapacity)));
    }

    public PlannerCapacitySnapshot ApplyMove(
        IReadOnlyList<SourceStackChange> sourceChanges,
        InventorySource destination,
        uint baseItemId,
        bool isHq,
        int quantity)
    {
        var updated = Clone();

        foreach (var change in sourceChanges)
            updated.ApplySourceChange(change);

        updated.ApplyDestinationChange(
            destination,
            baseItemId,
            isHq,
            quantity);

        return updated;
    }

    private PlannerCapacitySnapshot Clone() =>
        new(
            new Dictionary<LogicalStorageKey, int>(totalSlots),
            new Dictionary<LogicalStorageKey, int>(occupiedSlots),
            new Dictionary<LogicalStackKey, int>(mergeSpace),
            new Dictionary<uint, int>(maximumStacks));

    private void ApplySourceChange(
        SourceStackChange change)
    {
        if (!TryGetMaximumStack(
                change.Item.BaseItemId,
                out var maximumStack))
        {
            return;
        }

        var storageKey =
            ToLogicalStorageKey(change.Item);

        var stackKey =
            new LogicalStackKey(
                storageKey,
                change.Item.BaseItemId,
                change.Item.IsHq);

        var oldMergeSpace =
            Math.Max(
                0,
                maximumStack - change.Item.Quantity);

        var newMergeSpace =
            change.RemainingQuantity == 0
                ? 0
                : Math.Max(
                    0,
                    maximumStack - change.RemainingQuantity);

        SetMergeSpace(
            stackKey,
            mergeSpace.GetValueOrDefault(stackKey) -
            oldMergeSpace +
            newMergeSpace);

        if (change.RemainingQuantity == 0)
        {
            occupiedSlots[storageKey] =
                Math.Max(
                    0,
                    occupiedSlots.GetValueOrDefault(storageKey) - 1);
        }
    }

    private void ApplyDestinationChange(
        InventorySource destination,
        uint baseItemId,
        bool isHq,
        int quantity)
    {
        if (quantity <= 0 ||
            !TryGetMaximumStack(
                baseItemId,
                out var maximumStack))
        {
            return;
        }

        var storageKey =
            ToLogicalStorageKey(destination);

        var stackKey =
            new LogicalStackKey(
                storageKey,
                baseItemId,
                isHq);

        var currentMergeSpace =
            mergeSpace.GetValueOrDefault(stackKey);

        var merged =
            Math.Min(
                quantity,
                currentMergeSpace);

        SetMergeSpace(
            stackKey,
            currentMergeSpace - merged);

        var remaining =
            quantity - merged;

        if (remaining <= 0)
            return;

        var newSlots =
            checked(
                (int)(((long)remaining + maximumStack - 1) /
                      maximumStack));

        occupiedSlots[storageKey] =
            occupiedSlots.GetValueOrDefault(storageKey) +
            newSlots;

        var finalStackQuantity =
            remaining -
            (newSlots - 1) * maximumStack;

        SetMergeSpace(
            stackKey,
            mergeSpace.GetValueOrDefault(stackKey) +
            maximumStack - finalStackQuantity);
    }

    private void SetMergeSpace(
        LogicalStackKey key,
        int value)
    {
        if (value <= 0)
            mergeSpace.Remove(key);
        else
            mergeSpace[key] = value;
    }

    private static int GetLogicalSlotCount(
        StorageType storage) =>
        storage switch
        {
            StorageType.CharacterInventory =>
                CharacterInventorySlots,

            StorageType.Retainer =>
                RetainerInventorySlots,

            StorageType.FreeCompanyChest =>
                FreeCompanyChestSlots,

            _ => 0
        };

    private static uint GetCanonicalContainer(
        StorageType storage) =>
        storage switch
        {
            StorageType.CharacterInventory =>
                (uint)GameInventoryType.Inventory1,

            StorageType.Retainer =>
                (uint)GameInventoryType.RetainerPage1,

            StorageType.FreeCompanyChest =>
                InventorySource.AnyFreeCompanyPageContainer,

            _ => 0
        };

    private static LogicalStorageKey ToLogicalStorageKey(
        InventorySource source) =>
        new(
            source.Storage,
            source.OwnerId);

    private static LogicalStorageKey ToLogicalStorageKey(
        InventoryItemSnapshot item) =>
        new(
            item.Storage,
            item.OwnerId);

    private static bool IsCapacityItem(
        InventoryItemSnapshot item) =>
        item.Storage switch
        {
            StorageType.CharacterInventory =>
                IsCharacterInventoryContainer(item.Container),

            StorageType.Retainer =>
                IsRetainerContainer(item.Container),

            StorageType.FreeCompanyChest =>
                IsFreeCompanyContainer(item.Container),

            _ => false
        };

    private static bool IsCharacterInventoryContainer(uint container) =>
        container == (uint)GameInventoryType.Inventory1 ||
        container == (uint)GameInventoryType.Inventory2 ||
        container == (uint)GameInventoryType.Inventory3 ||
        container == (uint)GameInventoryType.Inventory4;

    private static bool IsRetainerContainer(uint container) =>
        container == (uint)GameInventoryType.RetainerPage1 ||
        container == (uint)GameInventoryType.RetainerPage2 ||
        container == (uint)GameInventoryType.RetainerPage3 ||
        container == (uint)GameInventoryType.RetainerPage4 ||
        container == (uint)GameInventoryType.RetainerPage5 ||
        container == (uint)GameInventoryType.RetainerPage6 ||
        container == (uint)GameInventoryType.RetainerPage7;

    private static bool IsFreeCompanyContainer(uint container) =>
        container == (uint)GameInventoryType.FreeCompanyPage1 ||
        container == (uint)GameInventoryType.FreeCompanyPage2 ||
        container == (uint)GameInventoryType.FreeCompanyPage3 ||
        container == (uint)GameInventoryType.FreeCompanyPage4 ||
        container == (uint)GameInventoryType.FreeCompanyPage5;

    private readonly record struct LogicalStorageKey(
        StorageType Storage,
        ulong OwnerId);

    private readonly record struct LogicalStackKey(
        LogicalStorageKey Storage,
        uint BaseItemId,
        bool IsHq);
}

public sealed record SourceStackChange(
    InventoryItemSnapshot Item,
    int RemainingQuantity);
