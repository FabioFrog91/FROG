using CriticalCommonLib.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FROG.Core.Inventory;

public readonly record struct ExecutionOrderCoordinate(
    int SlotIndex,
    int ContainerIndex);

public sealed record ExecutionOrderCharacterRecord(
    ulong CharacterId,
    List<ExecutionOrderCoordinate> PlayerInventory,
    Dictionary<ulong, List<ExecutionOrderCoordinate>> Retainers);

/// <summary>
/// Persists the last valid visible inventory order observed from the game's
/// ItemOrderModule. This is observation state only: it never owns quantities,
/// routes or planner choices.
/// </summary>
public sealed class ExecutionOrderCatalog
{
    private const int CharacterContainerCount = 4;
    private const int CharacterSlotsPerContainer = 35;
    private const int RetainerContainerCount = 7;
    private const int RetainerSlotsPerContainer = 25;

    private readonly object syncLock = new();
    private readonly Dictionary<ulong, ExecutionOrderCharacterRecord> records = new();
    private bool isDirty;

    public bool IsDirty
    {
        get
        {
            lock (syncLock)
                return isDirty;
        }
    }

    public int CharacterCount
    {
        get
        {
            lock (syncLock)
                return records.Count;
        }
    }

    public bool Observe(
        ulong characterId,
        InventorySortOrder sortOrder)
    {
        if (characterId == 0)
            return false;

        lock (syncLock)
        {
            records.TryGetValue(
                characterId,
                out var existing);

            var playerInventory =
                existing?.PlayerInventory.ToList() ??
                new List<ExecutionOrderCoordinate>();

            var retainers =
                existing?.Retainers.ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value.ToList()) ??
                new Dictionary<ulong, List<ExecutionOrderCoordinate>>();

            var changed = false;

            if (sortOrder.NormalInventories.TryGetValue(
                    "PlayerInventory",
                    out var playerCoordinates))
            {
                var captured =
                    CaptureValid(
                        playerCoordinates,
                        CharacterContainerCount,
                        CharacterSlotsPerContainer);

                if (captured is not null &&
                    !playerInventory.SequenceEqual(
                        captured))
                {
                    playerInventory = captured;
                    changed = true;
                }
            }

            foreach (var entry in sortOrder.RetainerInventories)
            {
                var captured =
                    CaptureValid(
                        entry.Value.InventoryCoords,
                        RetainerContainerCount,
                        RetainerSlotsPerContainer);

                if (captured is null)
                    continue;

                if (!retainers.TryGetValue(
                        entry.Key,
                        out var previous) ||
                    !previous.SequenceEqual(
                        captured))
                {
                    retainers[entry.Key] = captured;
                    changed = true;
                }
            }

            if (!changed)
                return false;

            records[characterId] =
                new ExecutionOrderCharacterRecord(
                    characterId,
                    playerInventory,
                    retainers);

            isDirty = true;
            return true;
        }
    }

    public bool TryGetCharacterInventory(
        ulong characterId,
        out IReadOnlyList<ExecutionOrderCoordinate> coordinates)
    {
        lock (syncLock)
        {
            if (records.TryGetValue(
                    characterId,
                    out var record) &&
                IsValid(
                    record.PlayerInventory,
                    CharacterContainerCount,
                    CharacterSlotsPerContainer))
            {
                coordinates =
                    record.PlayerInventory.ToArray();

                return true;
            }
        }

        coordinates =
            Array.Empty<ExecutionOrderCoordinate>();

        return false;
    }

    public bool TryGetRetainer(
        ulong parentCharacterId,
        ulong retainerId,
        out IReadOnlyList<ExecutionOrderCoordinate> coordinates)
    {
        lock (syncLock)
        {
            if (records.TryGetValue(
                    parentCharacterId,
                    out var record) &&
                record.Retainers.TryGetValue(
                    retainerId,
                    out var retainerCoordinates) &&
                IsValid(
                    retainerCoordinates,
                    RetainerContainerCount,
                    RetainerSlotsPerContainer))
            {
                coordinates =
                    retainerCoordinates.ToArray();

                return true;
            }
        }

        coordinates =
            Array.Empty<ExecutionOrderCoordinate>();

        return false;
    }

    public void SaveToDisk(
        string filePath)
    {
        List<ExecutionOrderCharacterRecord> snapshot;

        lock (syncLock)
        {
            snapshot =
                records.Values
                    .OrderBy(record =>
                        record.CharacterId)
                    .Select(record =>
                        new ExecutionOrderCharacterRecord(
                            record.CharacterId,
                            record.PlayerInventory.ToList(),
                            record.Retainers.ToDictionary(
                                entry => entry.Key,
                                entry => entry.Value.ToList())))
                    .ToList();
        }

        var directory =
            Path.GetDirectoryName(
                filePath);

        if (!string.IsNullOrWhiteSpace(
                directory))
        {
            Directory.CreateDirectory(
                directory);
        }

        var json =
            JsonSerializer.Serialize(
                snapshot,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });

        File.WriteAllText(
            filePath,
            json);

        lock (syncLock)
            isDirty = false;
    }

    public void LoadFromDisk(
        string filePath)
    {
        if (!File.Exists(
                filePath))
        {
            return;
        }

        var json =
            File.ReadAllText(
                filePath);

        var loaded =
            JsonSerializer.Deserialize<List<ExecutionOrderCharacterRecord>>(
                json);

        if (loaded is null)
            return;

        lock (syncLock)
        {
            records.Clear();

            foreach (var record in loaded)
            {
                var playerInventory =
                    IsValid(
                        record.PlayerInventory,
                        CharacterContainerCount,
                        CharacterSlotsPerContainer)
                        ? record.PlayerInventory.ToList()
                        : new List<ExecutionOrderCoordinate>();

                var retainers =
                    record.Retainers
                        .Where(entry =>
                            IsValid(
                                entry.Value,
                                RetainerContainerCount,
                                RetainerSlotsPerContainer))
                        .ToDictionary(
                            entry => entry.Key,
                            entry => entry.Value.ToList());

                if (playerInventory.Count == 0 &&
                    retainers.Count == 0)
                {
                    continue;
                }

                records[record.CharacterId] =
                    new ExecutionOrderCharacterRecord(
                        record.CharacterId,
                        playerInventory,
                        retainers);
            }

            isDirty = false;
        }
    }

    private static List<ExecutionOrderCoordinate>? CaptureValid(
        IReadOnlyList<(int slotIndex, int containerIndex)> coordinates,
        int containerCount,
        int slotsPerContainer)
    {
        var captured =
            coordinates
                .Select(coordinate =>
                    new ExecutionOrderCoordinate(
                        coordinate.slotIndex,
                        coordinate.containerIndex))
                .ToList();

        return IsValid(
            captured,
            containerCount,
            slotsPerContainer)
                ? captured
                : null;
    }

    private static bool IsValid(
        IReadOnlyList<ExecutionOrderCoordinate> coordinates,
        int containerCount,
        int slotsPerContainer)
    {
        if (coordinates.Count !=
            containerCount *
            slotsPerContainer)
        {
            return false;
        }

        var seen =
            new HashSet<ExecutionOrderCoordinate>();

        foreach (var coordinate in coordinates)
        {
            if (coordinate.ContainerIndex < 0 ||
                coordinate.ContainerIndex >=
                    containerCount ||
                coordinate.SlotIndex < 0 ||
                coordinate.SlotIndex >=
                    slotsPerContainer ||
                !seen.Add(
                    coordinate))
            {
                return false;
            }
        }

        return true;
    }
}
