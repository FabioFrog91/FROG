using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FROG.Core.Inventory;

public sealed class InventoryIndex
{
    private const int AuditHistoryLimit = 50;

    private readonly object syncLock = new();
    private readonly List<InventoryItemSnapshot> items = new();
    private readonly List<InventoryIndexAuditEntry> auditHistory = new();
    private readonly Dictionary<(StorageType Storage, ulong OwnerId, uint Container), DateTime>
        sourceObservedAtUtc = new();

    public IReadOnlyList<InventoryItemSnapshot> Items
    {
        get
        {
            lock (syncLock)
            {
                return items.ToList();
            }
        }
    }

    public int Count
    {
        get
        {
            lock (syncLock)
            {
                return items.Count;
            }
        }
    }

    public bool IsDirty
    {
        get
        {
            lock (syncLock)
            {
                return isDirty;
            }
        }
    }

    private bool isDirty;

    public IReadOnlyList<InventoryIndexAuditEntry> AuditHistory
    {
        get
        {
            lock (syncLock)
            {
                return auditHistory
                    .Select(entry =>
                        entry with
                        {
                            FreeCompanyPages = entry.FreeCompanyPages.ToArray()
                        })
                    .ToList();
            }
        }
    }

    public bool ReplaceSource(
        InventorySource source,
        IEnumerable<InventoryItemSnapshot> snapshots,
        DateTime? observedAtUtc = null)
    {
        var newSnapshots = snapshots
            .Where(x =>
                x.Storage == source.Storage &&
                x.OwnerId == source.OwnerId &&
                x.Container == source.Container)
            .ToList();

        lock (syncLock)
        {
            var beforeFreeCompanyPages =
                BuildFreeCompanyPageAudit();

            // The provider has just observed this source.
            // Treat that observation as the source of truth and replace
            // the complete contents of the source, exactly as we do for
            // the current character inventory.
            //
            // This is important for stack splits/merges: the old slot
            // state must not participate in the new state.
            items.RemoveAll(x =>
                x.Storage == source.Storage &&
                x.OwnerId == source.OwnerId &&
                x.Container == source.Container);

            items.AddRange(newSnapshots);

            sourceObservedAtUtc[
                (source.Storage, source.OwnerId, source.Container)] =
                observedAtUtc ?? DateTime.UtcNow;

            isDirty = true;

            AddAuditEntry(
                $"REPLACE_SOURCE {source.Storage} owner={source.OwnerId} container={source.Container}",
                beforeFreeCompanyPages);

            return true;
        }
    }

    public bool ReplaceCharacterInventory(
        ulong characterId,
        IEnumerable<InventoryItemSnapshot> snapshots,
        DateTime? observedAtUtc = null)
    {
        var newSnapshots = snapshots
            .Where(x =>
                x.Storage == StorageType.CharacterInventory &&
                x.OwnerId == characterId)
            .ToList();

        lock (syncLock)
        {
            var observedContainers =
                items
                    .Where(item =>
                        item.Storage == StorageType.CharacterInventory &&
                        item.OwnerId == characterId)
                    .Select(item => item.Container)
                    .Union(newSnapshots.Select(item => item.Container))
                    .Distinct()
                    .ToArray();

            // Deliberately replace the complete current-character inventory
            // on every synchronization.
            //
            // We do not compare the old and new contents here because the
            // purpose of this method is to make GameInventory the source
            // of truth for the currently logged-in character.
            //
            // Other characters and other storage types are untouched.
            items.RemoveAll(x =>
                x.Storage == StorageType.CharacterInventory &&
                x.OwnerId == characterId);

            items.AddRange(newSnapshots);

            var characterObservedAtUtc =
                observedAtUtc ?? DateTime.UtcNow;

            foreach (var container in observedContainers)
            {
                sourceObservedAtUtc[
                    (StorageType.CharacterInventory, characterId, container)] =
                    characterObservedAtUtc;
            }

            isDirty = true;

            return true;
        }
    }

    public void ReplaceAll(IEnumerable<InventoryItemSnapshot> snapshots)
    {
        lock (syncLock)
        {
            items.Clear();
            items.AddRange(snapshots);
            sourceObservedAtUtc.Clear();
            isDirty = true;
        }
    }

    public void SaveToDisk(string filePath)
    {
        List<InventoryItemSnapshot> snapshots;

        lock (syncLock)
        {
            AddAuditEntry(
                "SAVE_TO_DISK",
                BuildFreeCompanyPageAudit());

            snapshots = items.ToList();
        }

        var directory = Path.GetDirectoryName(filePath);

        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(
            snapshots,
            new JsonSerializerOptions
            {
                WriteIndented = true
            });

        File.WriteAllText(filePath, json);

        lock (syncLock)
        {
            isDirty = false;
        }
    }

    public void LoadFromDisk(string filePath)
    {
        if (!File.Exists(filePath))
            return;

        var json = File.ReadAllText(filePath);

        var snapshots =
            JsonSerializer.Deserialize<List<InventoryItemSnapshot>>(json);

        if (snapshots == null)
            return;

        lock (syncLock)
        {
            var beforeFreeCompanyPages =
                BuildFreeCompanyPageAudit();

            items.Clear();
            items.AddRange(snapshots);
            sourceObservedAtUtc.Clear();
            isDirty = false;

            AddAuditEntry(
                "LOAD_FROM_DISK",
                beforeFreeCompanyPages);
        }
    }

    private IReadOnlyList<InventoryIndexFreeCompanyPageAudit> BuildFreeCompanyPageAudit() =>
        items
            .Where(item =>
                item.Storage == StorageType.FreeCompanyChest)
            .GroupBy(item =>
                new
                {
                    item.OwnerId,
                    item.Container
                })
            .OrderBy(group =>
                group.Key.OwnerId)
            .ThenBy(group =>
                group.Key.Container)
            .Select(group =>
                new InventoryIndexFreeCompanyPageAudit(
                    group.Key.OwnerId,
                    group.Key.Container,
                    group.Count(),
                    group.Sum(item => item.Quantity)))
            .ToArray();

    private void AddAuditEntry(
        string operation,
        IReadOnlyList<InventoryIndexFreeCompanyPageAudit> beforeFreeCompanyPages)
    {
        var afterFreeCompanyPages =
            BuildFreeCompanyPageAudit();

        auditHistory.Add(
            new InventoryIndexAuditEntry(
                System.DateTime.UtcNow,
                operation,
                beforeFreeCompanyPages.ToArray(),
                afterFreeCompanyPages.ToArray()));

        if (auditHistory.Count > AuditHistoryLimit)
        {
            auditHistory.RemoveRange(
                0,
                auditHistory.Count - AuditHistoryLimit);
        }
    }

    public DateTime? GetSourceObservedAtUtc(
        InventorySource source)
    {
        lock (syncLock)
        {
            return sourceObservedAtUtc.TryGetValue(
                (source.Storage, source.OwnerId, source.Container),
                out var observedAtUtc)
                ? observedAtUtc
                : null;
        }
    }

    public DateTime? GetCharacterInventoryObservedAtUtc(
        ulong characterId)
    {
        lock (syncLock)
        {
            var observations =
                sourceObservedAtUtc
                    .Where(entry =>
                        entry.Key.Storage == StorageType.CharacterInventory &&
                        entry.Key.OwnerId == characterId)
                    .Select(entry =>
                        entry.Value)
                    .ToList();

            return observations.Count == 0
                ? null
                : observations.Max();
        }
    }

    public DateTime? GetFreeCompanyObservedAtUtc(
        ulong freeCompanyId)
    {
        lock (syncLock)
        {
            var observations =
                sourceObservedAtUtc
                    .Where(entry =>
                        entry.Key.Storage == StorageType.FreeCompanyChest &&
                        entry.Key.OwnerId == freeCompanyId)
                    .Select(entry =>
                        entry.Value)
                    .ToList();

            return observations.Count == 0
                ? null
                : observations.Max();
        }
    }

    public int GetCharacterInventoryQuantity(
        ulong characterId,
        uint baseItemId,
        bool isHq)
    {
        lock (syncLock)
        {
            return items
                .Where(item =>
                    item.BaseItemId == baseItemId &&
                    item.IsHq == isHq &&
                    item.Storage == StorageType.CharacterInventory &&
                    item.OwnerId == characterId)
                .Sum(item =>
                    item.Quantity);
        }
    }

    public int GetFreeCompanyQuantity(
        ulong freeCompanyId,
        uint baseItemId,
        bool isHq)
    {
        lock (syncLock)
        {
            return items
                .Where(item =>
                    item.BaseItemId == baseItemId &&
                    item.IsHq == isHq &&
                    item.Storage == StorageType.FreeCompanyChest &&
                    item.OwnerId == freeCompanyId)
                .Sum(item =>
                    item.Quantity);
        }
    }

    public int GetQuantity(
        uint baseItemId,
        bool isHq,
        InventorySource source)
    {
        lock (syncLock)
        {
            return items
                .Where(item =>
                    item.BaseItemId == baseItemId &&
                    item.IsHq == isHq &&
                    item.Storage == source.Storage &&
                    item.OwnerId == source.OwnerId &&
                    item.Container == source.Container)
                .Sum(item => item.Quantity);
        }
    }

    public InventorySourceItemObservation ObserveItem(
        uint baseItemId,
        bool isHq,
        InventorySource source)
    {
        lock (syncLock)
        {
            var quantity = 0;
            var matchingStacks = 0;
            ulong layoutFingerprint = 0;

            foreach (var item in items)
            {
                if (item.BaseItemId != baseItemId ||
                    item.IsHq != isHq ||
                    item.Storage != source.Storage ||
                    item.OwnerId != source.OwnerId ||
                    item.Container != source.Container)
                {
                    continue;
                }

                quantity += item.Quantity;
                matchingStacks++;

                layoutFingerprint ^=
                    GetStackLayoutFingerprint(
                        item);
            }

            layoutFingerprint ^=
                unchecked(
                    (ulong)matchingStacks *
                    1099511628211UL);

            var observedAtUtc =
                sourceObservedAtUtc.TryGetValue(
                    (source.Storage, source.OwnerId, source.Container),
                    out var observed)
                    ? observed
                    : null;

            return new InventorySourceItemObservation(
                quantity,
                observedAtUtc,
                layoutFingerprint);
        }
    }

    private static ulong GetStackLayoutFingerprint(
        InventoryItemSnapshot item)
    {
        const ulong offsetBasis =
            14695981039346656037UL;

        const ulong prime =
            1099511628211UL;

        var fingerprint = offsetBasis;

        fingerprint =
            (fingerprint ^ item.RawItemId) * prime;

        fingerprint =
            (fingerprint ^ unchecked((uint)item.Quantity)) * prime;

        fingerprint =
            (fingerprint ^ item.Container) * prime;

        fingerprint =
            (fingerprint ^ unchecked((uint)item.Slot)) * prime;

        return fingerprint;
    }

    public IEnumerable<InventoryItemSnapshot> Find(uint baseItemId)
    {
        lock (syncLock)
        {
            return items
                .Where(x => x.BaseItemId == baseItemId)
                .ToList();
        }
    }

    public IEnumerable<InventoryItemSnapshot> FindNq(uint baseItemId)
    {
        lock (syncLock)
        {
            return items
                .Where(x =>
                    x.BaseItemId == baseItemId &&
                    !x.IsHq)
                .ToList();
        }
    }

    public IEnumerable<InventoryItemSnapshot> FindHq(uint baseItemId)
    {
        lock (syncLock)
        {
            return items
                .Where(x =>
                    x.BaseItemId == baseItemId &&
                    x.IsHq)
                .ToList();
        }
    }

    public int GetTotalQuantity(uint baseItemId)
    {
        lock (syncLock)
        {
            return items
                .Where(x => x.BaseItemId == baseItemId)
                .Sum(x => x.Quantity);
        }
    }

    public int GetNqQuantity(uint baseItemId)
    {
        lock (syncLock)
        {
            return items
                .Where(x =>
                    x.BaseItemId == baseItemId &&
                    !x.IsHq)
                .Sum(x => x.Quantity);
        }
    }

    public int GetHqQuantity(uint baseItemId)
    {
        lock (syncLock)
        {
            return items
                .Where(x =>
                    x.BaseItemId == baseItemId &&
                    x.IsHq)
                .Sum(x => x.Quantity);
        }
    }

    public int GetTotalQuantity(
        uint baseItemId,
        InventorySource source)
    {
        lock (syncLock)
        {
            return items
                .Where(x =>
                    x.BaseItemId == baseItemId &&
                    x.Storage == source.Storage &&
                    x.OwnerId == source.OwnerId &&
                    x.Container == source.Container)
                .Sum(x => x.Quantity);
        }
    }

    public int GetNqQuantity(
        uint baseItemId,
        InventorySource source)
    {
        lock (syncLock)
        {
            return items
                .Where(x =>
                    x.BaseItemId == baseItemId &&
                    !x.IsHq &&
                    x.Storage == source.Storage &&
                    x.OwnerId == source.OwnerId &&
                    x.Container == source.Container)
                .Sum(x => x.Quantity);
        }
    }

    public int GetHqQuantity(
        uint baseItemId,
        InventorySource source)
    {
        lock (syncLock)
        {
            return items
                .Where(x =>
                    x.BaseItemId == baseItemId &&
                    x.IsHq &&
                    x.Storage == source.Storage &&
                    x.OwnerId == source.OwnerId &&
                    x.Container == source.Container)
                .Sum(x => x.Quantity);
        }
    }

    public IEnumerable<InventoryItemSnapshot> Find(
        InventorySource source,
        bool isHq)
    {
        lock (syncLock)
        {
            return items
                .Where(x =>
                    x.BaseItemId != 0 &&
                    x.IsHq == isHq &&
                    x.Storage == source.Storage &&
                    x.OwnerId == source.OwnerId &&
                    x.Container == source.Container)
                .ToList();
        }
    }

    private static string CreateComparisonKey(
        InventoryItemSnapshot item)
    {
        return string.Join(
            "|",
            item.BaseItemId,
            item.RawItemId,
            item.Quantity,
            item.IsHq,
            item.Storage,
            item.OwnerId,
            item.Container,
            item.Slot);
    }
}


public sealed record InventoryIndexFreeCompanyPageAudit(
    ulong FreeCompanyId,
    uint Container,
    int SnapshotCount,
    int Quantity);

public sealed record InventoryIndexAuditEntry(
    System.DateTime AtUtc,
    string Operation,
    IReadOnlyList<InventoryIndexFreeCompanyPageAudit> BeforeFreeCompanyPages,
    IReadOnlyList<InventoryIndexFreeCompanyPageAudit> FreeCompanyPages);

public readonly record struct InventorySourceItemObservation(
    int Quantity,
    DateTime? ObservedAtUtc,
    ulong LayoutFingerprint);
