using CriticalCommonLib.Services;
using Dalamud.Game.Inventory;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

/// <summary>
/// Builds the set of planner-visible inventory sources for a main character.
/// This owns source eligibility, character/retainer relationships, shared FC
/// routing context, and source identity normalization.
/// </summary>
public sealed class PlannerSourceBuilder
{
    private readonly CharacterCatalog characterCatalog;
    private readonly ICharacterMonitor characterMonitor;

    public PlannerSourceBuilder(
        CharacterCatalog characterCatalog,
        ICharacterMonitor characterMonitor)
    {
        this.characterCatalog = characterCatalog;
        this.characterMonitor = characterMonitor;
    }

    public IReadOnlyList<InventorySource> Build(
        IReadOnlyList<InventoryItemSnapshot> indexItems,
        ulong mainCharacterId)
    {
        var sources =
            new List<InventorySource>();

        var allowedCharacterIds =
            GetPlannerCharacterIds(
                    mainCharacterId)
                .ToHashSet();

        if (allowedCharacterIds.Count == 0)
            allowedCharacterIds.Add(mainCharacterId);

        var mainFreeCompanyId =
            GetCharacterFreeCompanyId(
                mainCharacterId);

        foreach (var snapshot in indexItems)
        {
            var source =
                CreateSource(snapshot);

            if (!IsPlannerSourceAllowed(
                    source,
                    allowedCharacterIds,
                    mainFreeCompanyId))
            {
                continue;
            }

            AddPlannerSource(
                sources,
                source);
        }

        foreach (var characterId in allowedCharacterIds)
        {
            AddCharacterInventoryDestination(
                sources,
                characterId);
        }

        if (mainFreeCompanyId != 0)
        {
            AddFreeCompanyHub(
                sources,
                mainFreeCompanyId);
        }

        return sources;
    }

    private IEnumerable<ulong> GetPlannerCharacterIds(
        ulong mainCharacterId)
    {
        yield return mainCharacterId;

        var mainFreeCompanyId =
            GetCharacterFreeCompanyId(
                mainCharacterId);

        if (mainFreeCompanyId == 0)
            yield break;

        foreach (var identity in characterCatalog.Entries)
        {
            if (identity.Type != CharacterIdentityType.Character)
                continue;

            if (identity.CharacterId == 0 ||
                identity.CharacterId == mainCharacterId)
            {
                continue;
            }

            if (identity.FreeCompanyId != mainFreeCompanyId)
                continue;

            yield return identity.CharacterId;
        }
    }

    private ulong GetCharacterFreeCompanyId(
        ulong characterId)
    {
        if (characterCatalog.TryGet(
                characterId,
                out var identity) &&
            identity.Type == CharacterIdentityType.Character &&
            identity.FreeCompanyId != 0)
        {
            return identity.FreeCompanyId;
        }

        return characterMonitor
            .GetCharacterById(characterId)
            ?.FreeCompanyId ?? 0;
    }

    private InventorySource CreateSource(
        InventoryItemSnapshot snapshot)
    {
        var parentCharacterId =
            snapshot.ParentCharacterId;

        if (snapshot.Storage == StorageType.FreeCompanyChest)
        {
            parentCharacterId = 0;
        }
        else if (parentCharacterId == 0 &&
                 snapshot.Storage == StorageType.Retainer)
        {
            parentCharacterId =
                characterCatalog.GetParentCharacterId(
                    snapshot.OwnerId);
        }

        if (parentCharacterId == 0 &&
            snapshot.Storage == StorageType.Retainer)
        {
            parentCharacterId =
                characterMonitor
                    .GetParentCharacterById(
                        snapshot.OwnerId)
                    ?.CharacterId ?? 0;
        }

        return new InventorySource(
            snapshot.Storage,
            snapshot.OwnerId,
            snapshot.Container,
            parentCharacterId,
            GetOwnerName(snapshot.Storage, snapshot.OwnerId));
    }

    private static bool IsPlannerSourceAllowed(
        InventorySource source,
        IReadOnlySet<ulong> allowedCharacterIds,
        ulong mainFreeCompanyId)
    {
        return source.Storage switch
        {
            StorageType.CharacterInventory =>
                allowedCharacterIds.Contains(
                    source.OwnerId),

            StorageType.Retainer =>
                allowedCharacterIds.Contains(
                    source.ParentCharacterId),

            StorageType.FreeCompanyChest =>
                mainFreeCompanyId != 0 &&
                source.OwnerId == mainFreeCompanyId,

            _ =>
                false
        };
    }

    private void AddCharacterInventoryDestination(
        List<InventorySource> sources,
        ulong characterId)
    {
        AddPlannerSource(
            sources,
            new InventorySource(
                StorageType.CharacterInventory,
                characterId,
                (uint)GameInventoryType.Inventory1,
                OwnerName:
                    GetOwnerName(
                        StorageType.CharacterInventory,
                        characterId)));
    }

    private void AddFreeCompanyHub(
        List<InventorySource> sources,
        ulong freeCompanyId)
    {
        AddPlannerSource(
            sources,
            new InventorySource(
                StorageType.FreeCompanyChest,
                freeCompanyId,
                (uint)GameInventoryType.FreeCompanyPage1,
                ParentCharacterId: 0,
                OwnerName:
                    GetOwnerName(
                        StorageType.FreeCompanyChest,
                        freeCompanyId)));
    }

    private static void AddPlannerSource(
        List<InventorySource> sources,
        InventorySource source)
    {
        var existingIndex =
            sources.FindIndex(existing =>
                existing.Storage == source.Storage &&
                existing.OwnerId == source.OwnerId &&
                existing.Container == source.Container &&
                existing.ParentCharacterId == source.ParentCharacterId);

        if (existingIndex < 0)
        {
            sources.Add(source);
            return;
        }

        if (string.IsNullOrWhiteSpace(
                sources[existingIndex].OwnerName) &&
            !string.IsNullOrWhiteSpace(
                source.OwnerName))
        {
            sources[existingIndex] = source;
        }
    }

    private string GetOwnerName(
        StorageType storage,
        ulong ownerId)
    {
        var catalogName =
            characterCatalog.GetName(ownerId);

        if (!string.IsNullOrWhiteSpace(catalogName))
            return catalogName;

        var monitorName =
            characterMonitor.GetCharacterNameById(
                ownerId);

        if (!string.IsNullOrWhiteSpace(monitorName))
            return monitorName;

        var character =
            characterMonitor.GetCharacterById(
                ownerId);

        if (character is null)
            return string.Empty;

        if (storage == StorageType.FreeCompanyChest)
        {
            var freeCompanyName =
                character.Name.ToString();

            return string.IsNullOrWhiteSpace(freeCompanyName)
                ? string.Empty
                : freeCompanyName;
        }

        return string.IsNullOrWhiteSpace(
            character.FormattedName)
            ? string.Empty
            : character.FormattedName;
    }
}
