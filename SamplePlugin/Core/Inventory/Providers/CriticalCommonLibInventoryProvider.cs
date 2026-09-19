using System;
using System.Collections.Generic;
using CriticalCommonLib.Services;
using Dalamud.Game.Inventory;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace FROG.Core.Inventory.Providers;

public sealed record RawRetainerItemDiagnostic(
    ulong RetainerId,
    InventoryType ContainerType,
    int Slot,
    uint RawItemId,
    uint BaseItemId,
    int Quantity,
    bool IsHq
);

public sealed record RetainerScannerComparisonDiagnostic(
    ulong RetainerId,
    InventoryType ContainerType,
    int Slot,
    uint GetInventoryRawItemId,
    int GetInventoryQuantity,
    uint RetainerBagRawItemId,
    int RetainerBagQuantity,
    bool SameItem,
    bool SameQuantity
);

public sealed class CriticalCommonLibInventoryProvider
    : ICriticalCommonLibInventoryProvider
{
    private static readonly object diagnosticLock = new();

    private static List<RawRetainerItemDiagnostic> lastRawRetainerItems = new();

    private static List<RetainerScannerComparisonDiagnostic>
        lastRetainerScannerComparisons = new();

    private readonly ICharacterMonitor characterMonitor;
    private readonly IInventoryScanner inventoryScanner;

    public static IReadOnlyList<RawRetainerItemDiagnostic> LastRawRetainerItems
    {
        get
        {
            lock (diagnosticLock)
            {
                return new List<RawRetainerItemDiagnostic>(
                    lastRawRetainerItems);
            }
        }
    }

    public static IReadOnlyList<RetainerScannerComparisonDiagnostic>
        LastRetainerScannerComparisons
    {
        get
        {
            lock (diagnosticLock)
            {
                return new List<RetainerScannerComparisonDiagnostic>(
                    lastRetainerScannerComparisons);
            }
        }
    }

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

        SetRawRetainerDiagnostics(
            Array.Empty<RawRetainerItemDiagnostic>());

        SetRetainerScannerComparisonDiagnostics(
            Array.Empty<RetainerScannerComparisonDiagnostic>());

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
        var rawDiagnosticList =
            new List<RawRetainerItemDiagnostic>();

        var comparisonDiagnosticList =
            new List<RetainerScannerComparisonDiagnostic>();

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

            AddScannerComparisonDiagnostics(
                comparisonDiagnosticList,
                retainerId,
                containerType,
                items);

            AddSnapshots(
                snapshotList,
                items,
                source,
                observedAtUtc,
                rawDiagnosticList,
                containerType);
        }

        SetRawRetainerDiagnostics(rawDiagnosticList);
        SetRetainerScannerComparisonDiagnostics(
            comparisonDiagnosticList);

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

        var sourceList = new List<InventorySource>();
        var snapshotList = new List<InventoryItemSnapshot>();

        var freeCompanyPages = new[]
        {
            InventoryType.FreeCompanyPage1,
            InventoryType.FreeCompanyPage2,
            InventoryType.FreeCompanyPage3,
            InventoryType.FreeCompanyPage4,
            InventoryType.FreeCompanyPage5
        };

        foreach (var containerType in freeCompanyPages)
        {
            if (!inventoryScanner.InMemory.Contains(containerType))
                continue;

            var source = new InventorySource(
                StorageType.FreeCompanyChest,
                freeCompanyId,
                (uint)containerType,
                characterMonitor.ActiveCharacterId);

            sourceList.Add(source);

            var items =
                inventoryScanner.GetInventoryByType(
                    containerType);

            AddSnapshots(
                snapshotList,
                items,
                source,
                observedAtUtc);
        }

        sources = sourceList;
        snapshots = snapshotList;

        return sourceList.Count > 0;
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

    private static bool IsRetainerItemContainer(
        InventoryType containerType)
    {
        return containerType >= InventoryType.RetainerPage1 &&
               containerType <= InventoryType.RetainerPage7;
    }

    private void AddScannerComparisonDiagnostics(
        List<RetainerScannerComparisonDiagnostic> diagnostics,
        ulong retainerId,
        InventoryType containerType,
        InventoryItem[] getInventoryItems)
    {
        var retainerBagItems =
            GetRetainerBagItems(
                retainerId,
                containerType);

        if (retainerBagItems == null)
            return;

        var maxLength =
            Math.Max(
                getInventoryItems.Length,
                retainerBagItems.Length);

        for (var slot = 0; slot < maxLength; slot++)
        {
            var getInventoryItem =
                slot < getInventoryItems.Length
                    ? getInventoryItems[slot]
                    : default;

            var retainerBagItem =
                slot < retainerBagItems.Length
                    ? retainerBagItems[slot]
                    : default;

            if (getInventoryItem.ItemId == 0 &&
                retainerBagItem.ItemId == 0)
            {
                continue;
            }

            diagnostics.Add(
                new RetainerScannerComparisonDiagnostic(
                    retainerId,
                    containerType,
                    slot,
                    getInventoryItem.ItemId,
                    getInventoryItem.Quantity,
                    retainerBagItem.ItemId,
                    retainerBagItem.Quantity,
                    getInventoryItem.ItemId ==
                        retainerBagItem.ItemId,
                    getInventoryItem.Quantity ==
                        retainerBagItem.Quantity));
        }
    }

    private InventoryItem[]? GetRetainerBagItems(
        ulong retainerId,
        InventoryType containerType)
    {
        if (retainerId != characterMonitor.ActiveRetainerId)
            return null;

        return containerType switch
        {
            InventoryType.RetainerPage1 =>
                inventoryScanner.RetainerBag1.TryGetValue(
                    retainerId,
                    out var page1)
                    ? page1
                    : null,

            InventoryType.RetainerPage2 =>
                inventoryScanner.RetainerBag2.TryGetValue(
                    retainerId,
                    out var page2)
                    ? page2
                    : null,

            InventoryType.RetainerPage3 =>
                inventoryScanner.RetainerBag3.TryGetValue(
                    retainerId,
                    out var page3)
                    ? page3
                    : null,

            InventoryType.RetainerPage4 =>
                inventoryScanner.RetainerBag4.TryGetValue(
                    retainerId,
                    out var page4)
                    ? page4
                    : null,

            InventoryType.RetainerPage5 =>
                inventoryScanner.RetainerBag5.TryGetValue(
                    retainerId,
                    out var page5)
                    ? page5
                    : null,

            _ => null
        };
    }

    private static void AddSnapshots(
        List<InventoryItemSnapshot> snapshots,
        InventoryItem[] items,
        InventorySource source,
        DateTime observedAtUtc,
        List<RawRetainerItemDiagnostic>? rawDiagnostics = null,
        InventoryType? containerType = null)
    {
        for (var slot = 0; slot < items.Length; slot++)
        {
            var item = items[slot];

            if (item.ItemId == 0)
                continue;

            var baseItem = ItemUtil.GetBaseId(item.ItemId);
            var isHq = baseItem.Kind == ItemKind.Hq;

            rawDiagnostics?.Add(
                new RawRetainerItemDiagnostic(
                    source.OwnerId,
                    containerType ?? (InventoryType)source.Container,
                    slot,
                    item.ItemId,
                    baseItem.ItemId,
                    item.Quantity,
                    isHq));

            snapshots.Add(
                new InventoryItemSnapshot(
                    baseItem.ItemId,
                    item.ItemId,
                    item.Quantity,
                    isHq,
                    source.Storage,
                    source.OwnerId,
                    source.Container,
                    slot,
                    observedAtUtc,
                    true));
        }
    }

    private static void SetRawRetainerDiagnostics(
        IEnumerable<RawRetainerItemDiagnostic> diagnostics)
    {
        lock (diagnosticLock)
        {
            lastRawRetainerItems =
                new List<RawRetainerItemDiagnostic>(diagnostics);
        }
    }

    private static void SetRetainerScannerComparisonDiagnostics(
        IEnumerable<RetainerScannerComparisonDiagnostic> diagnostics)
    {
        lock (diagnosticLock)
        {
            lastRetainerScannerComparisons =
                new List<RetainerScannerComparisonDiagnostic>(
                    diagnostics);
        }
    }
}
