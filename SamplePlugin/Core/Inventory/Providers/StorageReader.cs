using System;
using System.Collections.Generic;
using CriticalCommonLib.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace FROG.Core.Inventory.Providers;

public sealed class StorageReader
    : StorageReaderAPI
{
    private static readonly InventoryType[] RetainerPageTypes =
    {
        InventoryType.RetainerPage1,
        InventoryType.RetainerPage2,
        InventoryType.RetainerPage3,
        InventoryType.RetainerPage4,
        InventoryType.RetainerPage5,
        InventoryType.RetainerPage6,
        InventoryType.RetainerPage7
    };

    private readonly ICharacterMonitor characterMonitor;
    private readonly IInventoryScanner inventoryScanner;

    public StorageReader(
        ICharacterMonitor characterMonitor,
        IInventoryScanner inventoryScanner)
    {
        this.characterMonitor = characterMonitor;
        this.inventoryScanner = inventoryScanner;
    }

    public unsafe bool TryReadActiveRetainer(
        DateTime observedAtUtc,
        out IReadOnlyList<InventorySource> sources,
        out IReadOnlyList<InventoryItemSnapshot> snapshots)
    {
        sources = Array.Empty<InventorySource>();
        snapshots = Array.Empty<InventoryItemSnapshot>();

        var retainerId = characterMonitor.ActiveRetainerId;

        if (retainerId == 0)
            return false;

        var inventoryManager = InventoryManager.Instance();

        if (inventoryManager == null)
            return false;

        var parentCharacterId =
            characterMonitor.GetParentCharacterById(retainerId)?.CharacterId ?? 0;

        var sourceList = new List<InventorySource>();
        var snapshotList = new List<InventoryItemSnapshot>();

        foreach (var containerType in RetainerPageTypes)
        {
            var container =
                inventoryManager->GetInventoryContainer(containerType);

            if (container == null || !container->IsLoaded)
            {
                sources = Array.Empty<InventorySource>();
                snapshots = Array.Empty<InventoryItemSnapshot>();
                return false;
            }

            var source = new InventorySource(
                StorageType.Retainer,
                retainerId,
                (uint)containerType,
                parentCharacterId);

            sourceList.Add(source);

            for (var slot = 0; slot < container->Size; slot++)
            {
                var item = container->Items[slot];

                if (item.ItemId == 0)
                    continue;

                var baseItem = ItemUtil.GetBaseId(item.ItemId);
                var isHq = item.Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);

                snapshotList.Add(
                    new InventoryItemSnapshot(
                        baseItem.ItemId,
                        item.ItemId,
                        item.Quantity,
                        isHq,
                        StorageType.Retainer,
                        retainerId,
                        (uint)containerType,
                        slot,
                        observedAtUtc,
                        true,
                        parentCharacterId));
            }
        }

        sources = sourceList;
        snapshots = snapshotList;

        return true;
    }

    public bool TryReadObservedFreeCompanyPage(
        DateTime observedAtUtc,
        uint container,
        out InventorySource source,
        out IReadOnlyList<InventoryItemSnapshot> snapshots)
    {
        source = default!;
        snapshots = Array.Empty<InventoryItemSnapshot>();

        var containerType =
            (InventoryType)container;

        if (containerType < InventoryType.FreeCompanyPage1 ||
            containerType > InventoryType.FreeCompanyPage5)
        {
            return false;
        }

        var freeCompanyId =
            characterMonitor.ActiveFreeCompanyId;

        // CCL now adds InMemory only after a successful RAW -> cache copy.
        // The post-copy scanner event remains the semantic trigger; this is
        // an additional fail-safe guard for accidental direct callers.
        if (freeCompanyId == 0 ||
            !inventoryScanner.InMemory.Contains(containerType))
        {
            return false;
        }

        source =
            new InventorySource(
                StorageType.FreeCompanyChest,
                freeCompanyId,
                container,
                ParentCharacterId: 0);

        var snapshotList =
            new List<InventoryItemSnapshot>();

        var cachedItems =
            inventoryScanner.GetInventoryByType(containerType);

        for (var slot = 0;
             slot < cachedItems.Length;
             slot++)
        {
            var item = cachedItems[slot];

            if (item.ItemId == 0)
                continue;

            var baseItem =
                ItemUtil.GetBaseId(item.ItemId);

            var isHq =
                item.Flags.HasFlag(
                    InventoryItem.ItemFlags.HighQuality);

            snapshotList.Add(
                new InventoryItemSnapshot(
                    baseItem.ItemId,
                    item.ItemId,
                    item.Quantity,
                    isHq,
                    StorageType.FreeCompanyChest,
                    freeCompanyId,
                    container,
                    slot,
                    observedAtUtc,
                    true,
                    ParentCharacterId: 0));
        }

        snapshots = snapshotList;

        return true;
    }
}
