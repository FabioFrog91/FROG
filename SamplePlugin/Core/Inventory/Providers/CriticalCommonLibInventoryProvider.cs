using System;
using System.Collections.Generic;
using CriticalCommonLib.Services;
using Dalamud.Game.Inventory;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace FROG.Core.Inventory.Providers;

public sealed class CriticalCommonLibInventoryProvider
    : ICriticalCommonLibInventoryProvider
{
    private readonly ICharacterMonitor characterMonitor;
    private readonly IInventoryScanner inventoryScanner;

    public CriticalCommonLibInventoryProvider(
        ICharacterMonitor characterMonitor,
        IInventoryScanner inventoryScanner)
    {
        this.characterMonitor = characterMonitor;
        this.inventoryScanner = inventoryScanner;
    }

    public bool TryReadActiveRetainer(
        DateTime observedAtUtc,
        out IReadOnlyList<InventorySource> sources,
        out IReadOnlyList<InventoryItemSnapshot> snapshots)
    {
        sources = Array.Empty<InventorySource>();
        snapshots = Array.Empty<InventoryItemSnapshot>();

        var retainerId = characterMonitor.ActiveRetainerId;

        if (retainerId == 0)
            return false;

        if (!inventoryScanner.InMemoryRetainers.TryGetValue(
                retainerId,
                out var loadedContainers))
        {
            return false;
        }

        if (!IsRetainerFullyLoaded(loadedContainers))
            return false;

        var sourceList = new List<InventorySource>();
        var snapshotList = new List<InventoryItemSnapshot>();

        foreach (var containerType in loadedContainers)
        {
            if (!IsRetainerItemContainer(containerType))
                continue;

            var source = new InventorySource(
                StorageType.Retainer,
                retainerId,
                (uint)containerType,
                characterMonitor.GetParentCharacterById(retainerId)?.CharacterId ?? 0);

            sourceList.Add(source);

            var items =
                inventoryScanner.GetInventoryByType(
                    retainerId,
                    containerType);

            AddSnapshots(
                snapshotList,
                items,
                source,
                observedAtUtc);
        }

        sources = sourceList;
        snapshots = snapshotList;

        return true;
    }

    public bool TryReadActiveFreeCompany(
        DateTime observedAtUtc,
        out IReadOnlyList<InventorySource> sources,
        out IReadOnlyList<InventoryItemSnapshot> snapshots)
    {
        sources = Array.Empty<InventorySource>();
        snapshots = Array.Empty<InventoryItemSnapshot>();

        var freeCompanyId =
            characterMonitor.ActiveFreeCompanyId;

        if (freeCompanyId == 0)
            return false;

        if (!IsFreeCompanyFullyLoaded(inventoryScanner.InMemory))
            return false;

        var sourceList = new List<InventorySource>();
        var snapshotList = new List<InventoryItemSnapshot>();

        foreach (var containerType in inventoryScanner.InMemory)
        {
            if (!IsFreeCompanyItemContainer(containerType))
                continue;

            var source = new InventorySource(
                StorageType.FreeCompanyChest,
                freeCompanyId,
                (uint)containerType,
                characterMonitor.ActiveCharacterId);

            sourceList.Add(source);

            var items =
                inventoryScanner.GetInventoryByType(
                    freeCompanyId,
                    containerType);

            AddSnapshots(
                snapshotList,
                items,
                source,
                observedAtUtc);
        }

        sources = sourceList;
        snapshots = snapshotList;

        return true;
    }

    private static bool IsRetainerFullyLoaded(
        HashSet<InventoryType> loadedContainers)
    {
        return loadedContainers.Contains(InventoryType.RetainerPage1) &&
               loadedContainers.Contains(InventoryType.RetainerPage2) &&
               loadedContainers.Contains(InventoryType.RetainerPage3) &&
               loadedContainers.Contains(InventoryType.RetainerPage4) &&
               loadedContainers.Contains(InventoryType.RetainerPage5) &&
               loadedContainers.Contains(InventoryType.RetainerEquippedItems) &&
               loadedContainers.Contains(InventoryType.RetainerMarket) &&
               loadedContainers.Contains(InventoryType.RetainerCrystals) &&
               loadedContainers.Contains(InventoryType.RetainerGil);
    }

    private static bool IsFreeCompanyFullyLoaded(
        HashSet<InventoryType> loadedContainers)
    {
        return loadedContainers.Contains(InventoryType.FreeCompanyPage1) &&
               loadedContainers.Contains(InventoryType.FreeCompanyPage2) &&
               loadedContainers.Contains(InventoryType.FreeCompanyPage3) &&
               loadedContainers.Contains(InventoryType.FreeCompanyPage4) &&
               loadedContainers.Contains(InventoryType.FreeCompanyPage5);
    }

    private static bool IsRetainerItemContainer(
        InventoryType containerType)
    {
        return containerType >= InventoryType.RetainerPage1 &&
               containerType <= InventoryType.RetainerPage7;
    }

    private static bool IsFreeCompanyItemContainer(
        InventoryType containerType)
    {
        return containerType >= InventoryType.FreeCompanyPage1 &&
               containerType <= InventoryType.FreeCompanyPage5;
    }

    private static void AddSnapshots(
        List<InventoryItemSnapshot> snapshots,
        InventoryItem[] items,
        InventorySource source,
        DateTime observedAtUtc)
    {
        for (var slot = 0; slot < items.Length; slot++)
        {
            var item = items[slot];

            if (item.ItemId == 0)
                continue;

            var baseItem = ItemUtil.GetBaseId(item.ItemId);

            snapshots.Add(
                new InventoryItemSnapshot(
                    baseItem.ItemId,
                    item.ItemId,
                    item.Quantity,
                    baseItem.Kind == ItemKind.Hq,
                    source.Storage,
                    source.OwnerId,
                    source.Container,
                    slot,
                    observedAtUtc,
                    true));
        }
    }
}
