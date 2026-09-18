using System.Collections.Generic;
using CriticalCommonLib.Services;
using Dalamud.Game.Inventory;

namespace FROG.Core.Inventory.Providers;

public sealed class CriticalCommonLibStorageSourceProvider
    : IStorageSourceProvider
{
    private readonly ICharacterMonitor characterMonitor;

    public CriticalCommonLibStorageSourceProvider(
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

    private static void AddCharacterInventorySources(
        List<InventorySource> sources,
        ulong characterId)
    {
        sources.Add(
            new InventorySource(
                StorageType.CharacterInventory,
                characterId,
                (uint)GameInventoryType.Inventory1));

        sources.Add(
            new InventorySource(
                StorageType.CharacterInventory,
                characterId,
                (uint)GameInventoryType.Inventory2));

        sources.Add(
            new InventorySource(
                StorageType.CharacterInventory,
                characterId,
                (uint)GameInventoryType.Inventory3));

        sources.Add(
            new InventorySource(
                StorageType.CharacterInventory,
                characterId,
                (uint)GameInventoryType.Inventory4));
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

            AddRetainerContainers(
                sources,
                retainerId,
                characterId);
        }
    }

    private static void AddRetainerContainers(
        List<InventorySource> sources,
        ulong retainerId,
        ulong characterId)
    {
        sources.Add(
            new InventorySource(
                StorageType.Retainer,
                retainerId,
                (uint)GameInventoryType.RetainerPage1,
                characterId));

        sources.Add(
            new InventorySource(
                StorageType.Retainer,
                retainerId,
                (uint)GameInventoryType.RetainerPage2,
                characterId));

        sources.Add(
            new InventorySource(
                StorageType.Retainer,
                retainerId,
                (uint)GameInventoryType.RetainerPage3,
                characterId));

        sources.Add(
            new InventorySource(
                StorageType.Retainer,
                retainerId,
                (uint)GameInventoryType.RetainerPage4,
                characterId));

        sources.Add(
            new InventorySource(
                StorageType.Retainer,
                retainerId,
                (uint)GameInventoryType.RetainerPage5,
                characterId));

        sources.Add(
            new InventorySource(
                StorageType.Retainer,
                retainerId,
                (uint)GameInventoryType.RetainerPage6,
                characterId));

        sources.Add(
            new InventorySource(
                StorageType.Retainer,
                retainerId,
                (uint)GameInventoryType.RetainerPage7,
                characterId));
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

        var freeCompanyId = character.FreeCompanyId;

        sources.Add(
            new InventorySource(
                StorageType.FreeCompanyChest,
                freeCompanyId,
                (uint)GameInventoryType.FreeCompanyPage1,
                characterId));

        sources.Add(
            new InventorySource(
                StorageType.FreeCompanyChest,
                freeCompanyId,
                (uint)GameInventoryType.FreeCompanyPage2,
                characterId));

        sources.Add(
            new InventorySource(
                StorageType.FreeCompanyChest,
                freeCompanyId,
                (uint)GameInventoryType.FreeCompanyPage3,
                characterId));

        sources.Add(
            new InventorySource(
                StorageType.FreeCompanyChest,
                freeCompanyId,
                (uint)GameInventoryType.FreeCompanyPage4,
                characterId));

        sources.Add(
            new InventorySource(
                StorageType.FreeCompanyChest,
                freeCompanyId,
                (uint)GameInventoryType.FreeCompanyPage5,
                characterId));
    }
}
