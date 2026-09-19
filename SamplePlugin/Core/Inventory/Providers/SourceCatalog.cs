using System.Collections.Generic;
using CriticalCommonLib.Services;
using Dalamud.Game.Inventory;

namespace FROG.Core.Inventory.Providers;

public sealed class SourceCatalog
    : SourceCatalogAPI
{
    private readonly ICharacterMonitor characterMonitor;

    public SourceCatalog(
        ICharacterMonitor characterMonitor)
    {
        this.characterMonitor = characterMonitor;
    }

    public IReadOnlyList<InventorySource> GetSourcesForCharacter(
        ulong characterId)
    {
        var sources = new List<InventorySource>();

        AddCharacterInventorySources(
            sources,
            characterId);

        AddRetainerSources(
            sources,
            characterId);

        AddFreeCompanySources(
            sources,
            characterId);

        return sources;
    }

    private void AddCharacterInventorySources(
        List<InventorySource> sources,
        ulong characterId)
    {
        var character =
            characterMonitor.GetCharacterById(characterId);

        var characterName =
            character?.FormattedName ?? "Unknown";

        sources.Add(
            new InventorySource(
                StorageType.CharacterInventory,
                characterId,
                (uint)GameInventoryType.Inventory1,
                OwnerName: characterName));

        sources.Add(
            new InventorySource(
                StorageType.CharacterInventory,
                characterId,
                (uint)GameInventoryType.Inventory2,
                OwnerName: characterName));

        sources.Add(
            new InventorySource(
                StorageType.CharacterInventory,
                characterId,
                (uint)GameInventoryType.Inventory3,
                OwnerName: characterName));

        sources.Add(
            new InventorySource(
                StorageType.CharacterInventory,
                characterId,
                (uint)GameInventoryType.Inventory4,
                OwnerName: characterName));
    }

    private void AddRetainerSources(
        List<InventorySource> sources,
        ulong characterId)
    {
        var retainers =
            characterMonitor.GetRetainerCharacters(characterId);

        foreach (var retainer in retainers)
        {
            var retainerId = retainer.Key;

            var retainerName =
                retainer.Value.FormattedName;

            AddRetainerContainers(
                sources,
                retainerId,
                characterId,
                retainerName);
        }
    }

    private static void AddRetainerContainers(
        List<InventorySource> sources,
        ulong retainerId,
        ulong characterId,
        string retainerName)
    {
        sources.Add(
            new InventorySource(
                StorageType.Retainer,
                retainerId,
                (uint)GameInventoryType.RetainerPage1,
                characterId,
                retainerName));

        sources.Add(
            new InventorySource(
                StorageType.Retainer,
                retainerId,
                (uint)GameInventoryType.RetainerPage2,
                characterId,
                retainerName));

        sources.Add(
            new InventorySource(
                StorageType.Retainer,
                retainerId,
                (uint)GameInventoryType.RetainerPage3,
                characterId,
                retainerName));

        sources.Add(
            new InventorySource(
                StorageType.Retainer,
                retainerId,
                (uint)GameInventoryType.RetainerPage4,
                characterId,
                retainerName));

        sources.Add(
            new InventorySource(
                StorageType.Retainer,
                retainerId,
                (uint)GameInventoryType.RetainerPage5,
                characterId,
                retainerName));

        sources.Add(
            new InventorySource(
                StorageType.Retainer,
                retainerId,
                (uint)GameInventoryType.RetainerPage6,
                characterId,
                retainerName));

        sources.Add(
            new InventorySource(
                StorageType.Retainer,
                retainerId,
                (uint)GameInventoryType.RetainerPage7,
                characterId,
                retainerName));
    }

    private void AddFreeCompanySources(
        List<InventorySource> sources,
        ulong characterId)
    {
        var character =
            characterMonitor.GetCharacterById(characterId);

        if (character is null ||
            character.FreeCompanyId == 0)
        {
            return;
        }

        var freeCompanyId =
            character.FreeCompanyId;

        var freeCompany =
            characterMonitor.GetCharacterById(freeCompanyId);

        var freeCompanyName =
            freeCompany?.FormattedName ?? "Unknown";

        sources.Add(
            new InventorySource(
                StorageType.FreeCompanyChest,
                freeCompanyId,
                (uint)GameInventoryType.FreeCompanyPage1,
                characterId,
                freeCompanyName));

        sources.Add(
            new InventorySource(
                StorageType.FreeCompanyChest,
                freeCompanyId,
                (uint)GameInventoryType.FreeCompanyPage2,
                characterId,
                freeCompanyName));

        sources.Add(
            new InventorySource(
                StorageType.FreeCompanyChest,
                freeCompanyId,
                (uint)GameInventoryType.FreeCompanyPage3,
                characterId,
                freeCompanyName));

        sources.Add(
            new InventorySource(
                StorageType.FreeCompanyChest,
                freeCompanyId,
                (uint)GameInventoryType.FreeCompanyPage4,
                characterId,
                freeCompanyName));

        sources.Add(
            new InventorySource(
                StorageType.FreeCompanyChest,
                freeCompanyId,
                (uint)GameInventoryType.FreeCompanyPage5,
                characterId,
                freeCompanyName));
    }
}
