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
    private readonly Dictionary<(StorageType Storage, ulong OwnerId, uint Container), long>
        sourceObservationRevision = new();
    private readonly Dictionary<(StorageType Storage, ulong OwnerId, uint Container), long>
        sourceContentRevision = new();
    private readonly Dictionary<(StorageType Storage, ulong OwnerId, uint Container), long>
        sourceProviderRevision = new();

    private long nextObservationRevision;
    private long nextContentRevision;
    private bool isDirty;

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

    public InventoryApplyObservationResult ApplyObservation(
        InventorySource source,
        IEnumerable<InventoryItemSnapshot> snapshots,
        long providerRevision,
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
            var key =
                (source.Storage, source.OwnerId, source.Container);

            if (sourceProviderRevision.TryGetValue(key, out var previousProviderRevision) &&
                providerRevision <= previousProviderRevision)
            {
                return new InventoryApplyObservationResult(
                    false,
                    false,
                    sourceObservationRevision.TryGetValue(key, out var existingObservationRevision)
                        ? existingObservationRevision
                        : 0,
                    sourceContentRevision.TryGetValue(key, out var existingContentRevision)
                        ? existingContentRevision
                        : 0);
            }

            var existingSnapshots = items
                .Where(x =>
                    x.Storage == source.Storage &&
                    x.OwnerId == source.OwnerId &&
                    x.Container == source.Container)
                .ToList();

            var contentChanged =
                !HaveSameContents(existingSnapshots, newSnapshots);

            var observationRevision =
                ++nextObservationRevision;

            sourceProviderRevision[key] = providerRevision;
            sourceObservationRevision[key] = observationRevision;
            sourceObservedAtUtc[key] = observedAtUtc ?? DateTime.UtcNow;

            var contentRevision =
                sourceContentRevision.TryGetValue(key, out var previousContentRevision)
                    ? previousContentRevision
                    : 0;

            if (contentChanged)
            {
                var beforeFreeCompanyPages =
                    BuildFreeCompanyPageAudit();

                items.RemoveAll(x =>
                    x.Storage == source.Storage &&
                    x.OwnerId == source.OwnerId &&
                    x.Container == source.Container);

                items.AddRange(newSnapshots);

                contentRevision = ++nextContentRevision;
                sourceContentRevision[key] = contentRevision;
                isDirty = true;

                AddAuditEntry(
                    $"APPLY_OBSERVATION {source.Storage} owner={source.OwnerId} container={source.Container} providerRevision={providerRevision}",
                    beforeFreeCompanyPages);
            }

            return new InventoryApplyObservationResult(
                true,
                contentChanged,
                observationRevision,
                contentRevision);
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

            items.RemoveAll(x =>
                x.Storage == source.Storage &&
                x.OwnerId == source.OwnerId &&
                x.Container == source.Container);

            items.AddRange(newSnapshots);

            var key =
                (source.Storage, source.OwnerId, source.Container);

            sourceObservedAtUtc[key] = observedAtUtc ?? DateTime.UtcNow;
            sourceObservationRevision[key] = ++nextObservationRevision;
            sourceContentRevision[key] = ++nextContentRevision;
            sourceProviderRevision.Remove(key);

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

            items.RemoveAll(x =>
                x.Storage == StorageType.CharacterInventory &&
                x.OwnerId == characterId);

            items.AddRange(newSnapshots);

            var characterObservedAtUtc =
                observedAtUtc ?? DateTime.UtcNow;
            var observationRevision =
                ++nextObservationRevision;
            var contentRevision =
                ++nextContentRevision;

            foreach (var container in observedContainers)
            {
                var key =
                    (StorageType.CharacterInventory, characterId, container);

                sourceObservedAtUtc[key] = characterObservedAtUtc;
                sourceObservationRevision[key] = observationRevision;
                sourceContentRevision[key] = contentRevision;
                sourceProviderRevision.Remove(key);
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
            ClearRuntimeObservationState();
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
            ClearRuntimeObservationState();
            isDirty = false;

            AddAuditEntry(
                "LOAD_FROM_DISK",
                beforeFreeCompanyPages);
        }
    }

    private void ClearRuntimeObservationState()
    {
        sourceObservedAtUtc.Clear();
        sourceObservationRevision.Clear();
        sourceContentRevision.Clear();
        sourceProviderRevision.Clear();
        nextObservationRevision = 0;
        nextContentRevision = 0;
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
                DateTime.UtcNow,
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

    public long? GetSourceObservationRevision(
        InventorySource source)
    {
        lock (syncLock)
        {
            return sourceObservationRevision.TryGetValue(
                (source.Storage, source.OwnerId, source.Container),
                out var revision)
                ? revision
                : null;
        }
    }

    public long GetSourceContentRevision(
        InventorySource source)
    {
        lock (syncLock)
        {
            return sourceContentRevision.TryGetValue(
                (source.Storage, source.OwnerId, source.Container),
                out var revision)
                ? revision
                : 0;
        }
    }

    public DateTime? GetCharacterInventoryObservedAtUtc(
        ulong characterId)
    {
        lock (syncLock)
        {
            var observations = sourceObservedAtUtc
                .Where(entry =>
                    entry.Key.Storage == StorageType.CharacterInventory &&
                    entry.Key.OwnerId == characterId)
                .Select(entry => entry.Value)
                .ToList();

            return observations.Count == 0
                ? null
                : observations.Max();
        }
    }

    public long? GetCharacterInventoryObservationRevision(
        ulong characterId)
    {
        lock (syncLock)
        {
            var revisions = sourceObservationRevision
                .Where(entry =>
                    entry.Key.Storage == StorageType.CharacterInventory &&
                    entry.Key.OwnerId == characterId)
                .Select(entry => entry.Value)
                .ToList();

            return revisions.Count == 0
                ? null
                : revisions.Max();
        }
    }

    public DateTime? GetFreeCompanyObservedAtUtc(
        ulong freeCompanyId)
    {
        lock (syncLock)
        {
            var observations = sourceObservedAtUtc
                .Where(entry =>
                    entry.Key.Storage == StorageType.FreeCompanyChest &&
                    entry.Key.OwnerId == freeCompanyId)
                .Select(entry => entry.Value)
                .ToList();

            return observations.Count == 0
                ? null
                : observations.Max();
        }
    }

    public long? GetFreeCompanyObservationRevision(
        ulong freeCompanyId)
    {
        lock (syncLock)
        {
            var revisions = sourceObservationRevision
                .Where(entry =>
                    entry.Key.Storage == StorageType.FreeCompanyChest &&
                    entry.Key.OwnerId == freeCompanyId)
                .Select(entry => entry.Value)
                .ToList();

            return revisions.Count == 0
                ? null
                : revisions.Max();
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
                .Sum(item => item.Quantity);
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
                .Sum(item => item.Quantity);
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
            var logicalQuantity = 0;
            var matchingStacks = 0;
            ulong layoutFingerprint = 0;

            foreach (var item in items)
            {
                if (item.BaseItemId != baseItemId ||
                    item.IsHq != isHq ||
                    item.Storage != source.Storage ||
                    item.OwnerId != source.OwnerId)
                {
                    continue;
                }

                logicalQuantity += item.Quantity;

                if (item.Container != source.Container)
                    continue;

                quantity += item.Quantity;
                matchingStacks++;

                layoutFingerprint ^=
                    GetStackLayoutFingerprint(item);
            }

            layoutFingerprint ^=
                unchecked((ulong)matchingStacks * 1099511628211UL);

            var key =
                (source.Storage, source.OwnerId, source.Container);

            var observedAtUtc =
                sourceObservedAtUtc.TryGetValue(key, out var observed)
                    ? (DateTime?)observed
                    : null;

            var observationRevision =
                sourceObservationRevision.TryGetValue(key, out var revision)
                    ? (long?)revision
                    : null;

            var contentRevision =
                sourceContentRevision.TryGetValue(key, out var contentRev)
                    ? contentRev
                    : 0;

            return new InventorySourceItemObservation(
                quantity,
                logicalQuantity,
                observedAtUtc,
                observationRevision,
                contentRevision,
                layoutFingerprint);
        }
    }

    private static bool HaveSameContents(
        IReadOnlyList<InventoryItemSnapshot> left,
        IReadOnlyList<InventoryItemSnapshot> right)
    {
        if (left.Count != right.Count)
            return false;

        var orderedLeft = left
            .OrderBy(CreateComparisonKey)
            .ToArray();
        var orderedRight = right
            .OrderBy(CreateComparisonKey)
            .ToArray();

        for (var index = 0; index < orderedLeft.Length; index++)
        {
            var a = orderedLeft[index];
            var b = orderedRight[index];

            if (a.BaseItemId != b.BaseItemId ||
                a.RawItemId != b.RawItemId ||
                a.Quantity != b.Quantity ||
                a.IsHq != b.IsHq ||
                a.Storage != b.Storage ||
                a.OwnerId != b.OwnerId ||
                a.Container != b.Container ||
                a.Slot != b.Slot ||
                a.IsVerified != b.IsVerified ||
                a.ParentCharacterId != b.ParentCharacterId)
            {
                return false;
            }
        }

        return true;
    }

    private static ulong GetStackLayoutFingerprint(
        InventoryItemSnapshot item)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        var fingerprint = offsetBasis;
        fingerprint = (fingerprint ^ item.RawItemId) * prime;
        fingerprint = (fingerprint ^ unchecked((uint)item.Quantity)) * prime;
        fingerprint = (fingerprint ^ item.Container) * prime;
        fingerprint = (fingerprint ^ unchecked((uint)item.Slot)) * prime;

        return fingerprint;
    }

    public IEnumerable<InventoryItemSnapshot> Find(uint baseItemId)
    {
        lock (syncLock)
        {
            return items.Where(x => x.BaseItemId == baseItemId).ToList();
        }
    }

    public IEnumerable<InventoryItemSnapshot> FindNq(uint baseItemId)
    {
        lock (syncLock)
        {
            return items
                .Where(x => x.BaseItemId == baseItemId && !x.IsHq)
                .ToList();
        }
    }

    public IEnumerable<InventoryItemSnapshot> FindHq(uint baseItemId)
    {
        lock (syncLock)
        {
            return items
                .Where(x => x.BaseItemId == baseItemId && x.IsHq)
                .ToList();
        }
    }

    public int GetTotalQuantity(uint baseItemId)
    {
        lock (syncLock)
        {
            return items.Where(x => x.BaseItemId == baseItemId).Sum(x => x.Quantity);
        }
    }

    public int GetNqQuantity(uint baseItemId)
    {
        lock (syncLock)
        {
            return items.Where(x => x.BaseItemId == baseItemId && !x.IsHq).Sum(x => x.Quantity);
        }
    }

    public int GetHqQuantity(uint baseItemId)
    {
        lock (syncLock)
        {
            return items.Where(x => x.BaseItemId == baseItemId && x.IsHq).Sum(x => x.Quantity);
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
            item.Slot,
            item.IsVerified,
            item.ParentCharacterId);
    }
}

public readonly record struct InventoryApplyObservationResult(
    bool Applied,
    bool ContentChanged,
    long ObservationRevision,
    long ContentRevision);

public sealed record InventoryIndexFreeCompanyPageAudit(
    ulong FreeCompanyId,
    uint Container,
    int SnapshotCount,
    int Quantity);

public sealed record InventoryIndexAuditEntry(
    DateTime AtUtc,
    string Operation,
    IReadOnlyList<InventoryIndexFreeCompanyPageAudit> BeforeFreeCompanyPages,
    IReadOnlyList<InventoryIndexFreeCompanyPageAudit> FreeCompanyPages);

public readonly record struct InventorySourceItemObservation(
    int Quantity,
    int LogicalQuantity,
    DateTime? ObservedAtUtc,
    long? ObservationRevision,
    long ContentRevision,
    ulong LayoutFingerprint);
