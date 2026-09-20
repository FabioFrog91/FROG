using CriticalCommonLib.Models;
using CriticalCommonLib.Services;

namespace FROG.Core.Inventory.Providers;

public sealed class CharacterCatalogSync
{
    private readonly ICharacterMonitor characterMonitor;
    private readonly CharacterCatalog catalog;

    public CharacterCatalogSync(
        ICharacterMonitor characterMonitor,
        CharacterCatalog catalog)
    {
        this.characterMonitor = characterMonitor;
        this.catalog = catalog;
    }

    public void SyncAll()
    {
        foreach (var character in characterMonitor.Characters.Values)
        {
            Sync(character);
        }
    }

    public void Sync(
        Character character)
    {
        if (character.CharacterId == 0)
            return;

        var type =
            character.CharacterType switch
            {
                CharacterType.Character =>
                    CharacterIdentityType.Character,

                CharacterType.Retainer =>
                    CharacterIdentityType.Retainer,

                CharacterType.FreeCompanyChest =>
                    CharacterIdentityType.FreeCompany,

                _ =>
                    return
            };

        var ownerCharacterId =
            type == CharacterIdentityType.Retainer
                ? character.OwnerId
                : 0;

        catalog.Upsert(
            new CharacterIdentity(
                character.CharacterId,
                type,
                character.FormattedName,
                character.WorldId,
                ownerCharacterId,
                character.FreeCompanyId));
    }
}
