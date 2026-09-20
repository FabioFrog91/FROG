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
        IEnumerable<InventoryItemSnapshot> snapshots)
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

            isDirty = true;

            AddAuditEntry(
                $"REPLACE_SOURCE {source.Storage} owner={source.OwnerId} container={source.Container}",
                beforeFreeCompanyPages);

            return true;
        }
    }

    public bool ReplaceCharacterInventory(
        ulong characterId,
        IEnumerable<InventoryItemSnapshot> snapshots)
    {
        var newSnapshots = snapshots
            .Where(x =>
                x.Storage == StorageType.CharacterInventory &&
                x.OwnerId == characterId)
            .ToList();

        lock (syncLock)
        {
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
