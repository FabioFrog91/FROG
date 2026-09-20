using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FROG.Core.Inventory;

public enum CharacterIdentityType
{
    Character,
    Retainer,
    FreeCompany
}

public sealed record CharacterIdentity(
    ulong CharacterId,
    CharacterIdentityType Type,
    string Name,
    uint WorldId,
    ulong OwnerCharacterId,
    ulong FreeCompanyId);

public sealed class CharacterCatalog
{
    private const int CurrentVersion = 1;

    private readonly Dictionary<ulong, CharacterIdentity> entries = new();

    public bool IsDirty { get; private set; }

    public IReadOnlyList<CharacterIdentity> Entries =>
        entries.Values
            .OrderBy(entry => entry.Type)
            .ThenBy(entry => entry.Name)
            .ThenBy(entry => entry.CharacterId)
            .ToList();

    public void Upsert(CharacterIdentity identity)
    {
        if (identity.CharacterId == 0)
            return;

        if (entries.TryGetValue(
                identity.CharacterId,
                out var existing))
        {
            identity = Merge(
                existing,
                identity);
        }

        if (entries.TryGetValue(
                identity.CharacterId,
                out var current) &&
            current == identity)
        {
            return;
        }

        entries[identity.CharacterId] = identity;
        IsDirty = true;
    }

    public bool TryGet(
        ulong characterId,
        out CharacterIdentity identity)
    {
        if (entries.TryGetValue(
                characterId,
                out var existing))
        {
            identity = existing;
            return true;
        }

        identity = null!;
        return false;
    }

    public ulong GetParentCharacterId(
        ulong characterId)
    {
        if (!entries.TryGetValue(
                characterId,
                out var identity))
        {
            return 0;
        }

        return identity.Type == CharacterIdentityType.Retainer
            ? identity.OwnerCharacterId
            : 0;
    }

    public string GetName(
        ulong characterId)
    {
        return entries.TryGetValue(
                characterId,
                out var identity) &&
            !string.IsNullOrWhiteSpace(identity.Name)
                ? identity.Name
                : string.Empty;
    }

    public void SaveToDisk(
        string filePath)
    {
        var data = new CharacterCatalogData
        {
            Version = CurrentVersion,
            Entries = Entries.ToList()
        };

        var directory = Path.GetDirectoryName(filePath);

        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var temporaryPath =
            filePath + ".tmp";

        var json = JsonSerializer.Serialize(
            data,
            new JsonSerializerOptions
            {
                WriteIndented = true
            });

        File.WriteAllText(
            temporaryPath,
            json);

        File.Move(
            temporaryPath,
            filePath,
            true);

        IsDirty = false;
    }

    public void LoadFromDisk(
        string filePath)
    {
        if (!File.Exists(filePath))
            return;

        var json =
            File.ReadAllText(filePath);

        var data =
            JsonSerializer.Deserialize<CharacterCatalogData>(
                json);

        if (data?.Entries is null)
            return;

        entries.Clear();

        foreach (var entry in data.Entries)
        {
            if (entry.CharacterId == 0)
                continue;

            entries[entry.CharacterId] = entry;
        }

        IsDirty = false;
    }

    private static CharacterIdentity Merge(
        CharacterIdentity existing,
        CharacterIdentity incoming)
    {
        return incoming with
        {
            Name = string.IsNullOrWhiteSpace(incoming.Name)
                ? existing.Name
                : incoming.Name,

            WorldId = incoming.WorldId != 0
                ? incoming.WorldId
                : existing.WorldId,

            OwnerCharacterId = incoming.OwnerCharacterId != 0
                ? incoming.OwnerCharacterId
                : existing.OwnerCharacterId,

            FreeCompanyId = incoming.FreeCompanyId != 0
                ? incoming.FreeCompanyId
                : existing.FreeCompanyId
        };
    }

    private sealed class CharacterCatalogData
    {
        public int Version { get; set; }

        public List<CharacterIdentity> Entries { get; set; } = new();
    }
}
