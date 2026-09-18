using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using CriticalCommonLib.Models;
using CriticalCommonLib.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Inventory;
using Dalamud.Interface.Windowing;
using FROG.Core.Inventory;
using Lumina.Excel.Sheets;

namespace FROG.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly ICharacterMonitor characterMonitor;

    private RequirementSet? importedRequirementSet;

    private int searchItemId;

    public MainWindow(
        Plugin plugin,
        ICharacterMonitor characterMonitor)
        : base("FROG")
    {
        this.plugin = plugin;
        this.characterMonitor = characterMonitor;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(500, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        ImGui.Text("FROG");
        ImGui.Separator();

        DrawBuildInfo();

        ImGui.Spacing();

        var playerState = Plugin.PlayerState;

        if (!playerState.IsLoaded)
        {
            ImGui.Text("Personaggio non disponibile.");
            return;
        }

        ImGui.Text("PERSONAGGIO");
        ImGui.Separator();

        ImGui.Text($"Nome: {playerState.CharacterName}");
        ImGui.Text($"Livello: {playerState.Level}");

        if (playerState.ClassJob.IsValid)
        {
            ImGui.Text($"Job: {playerState.ClassJob.Value.Abbreviation}");
        }

        ImGui.Text($"Content ID: {playerState.ContentId:X}");

        ImGui.Spacing();

        DrawTeamcraftImport();

        ImGui.Spacing();

        DrawResolverDiagnostics();

        ImGui.Spacing();

        ImGui.Text("CLIENT");
        ImGui.Separator();

        var territoryId = Plugin.ClientState.TerritoryType;

        ImGui.Text($"Territory ID: {territoryId}");

        if (Plugin.DataManager
            .GetExcelSheet<TerritoryType>()
            .TryGetRow(territoryId, out var territoryRow))
        {
            ImGui.Text(
                $"Zona: {territoryRow.PlaceName.Value.Name}");
        }

        ImGui.Spacing();

        ImGui.Text("INVENTARIO");
        ImGui.Separator();

        DrawInventoryContainer(
            "Inventory 1",
            GameInventoryType.Inventory1);

        DrawInventoryContainer(
            "Inventory 2",
            GameInventoryType.Inventory2);

        DrawInventoryContainer(
            "Inventory 3",
            GameInventoryType.Inventory3);

        DrawInventoryContainer(
            "Inventory 4",
            GameInventoryType.Inventory4);

        ImGui.Spacing();

        DrawStorageIndexDiagnostics();

        ImGui.Spacing();

        DrawSyncDiagnostics();

        ImGui.Spacing();

        ImGui.Text("RICERCA NELL'INDICE");
        ImGui.Separator();

        DrawIndexSearch();
    }

    private void DrawTeamcraftImport()
    {
        ImGui.Text("TEAMCRAFT IMPORT");
        ImGui.Separator();

        ImGui.TextWrapped(
            "Copia una lista Teamcraft negli appunti e importala direttamente in FROG.");

        if (ImGui.Button("Importa da Clipboard"))
        {
            var text = ImGui.GetClipboardText();

            var importer =
                new TeamcraftListImporter(
                    Plugin.DataManager);

            importedRequirementSet =
                importer.Import(text);
        }

        if (importedRequirementSet == null)
        {
            ImGui.Text("Nessuna lista importata.");
            return;
        }

        ImGui.Spacing();

        ImGui.Text(
            $"Requirement importati: {importedRequirementSet.Requirements.Count}");

        foreach (var requirement in importedRequirementSet.Requirements)
        {
            var quality =
                requirement.QualityPolicy.ToString();

            ImGui.Text(
                $"{GetItemName(requirement.BaseItemId)} | " +
                $"Qty {requirement.Quantity} | " +
                $"Quality {quality} | " +
                $"Precraft {requirement.IsPrecraft}");
        }
    }

    private void DrawResolverDiagnostics()
    {
        ImGui.Text("RESOLVER DEBUG");
        ImGui.Separator();

        if (importedRequirementSet == null)
        {
            ImGui.Text(
                "Importa prima una lista Teamcraft.");

            return;
        }

        if (importedRequirementSet.Requirements.Count == 0)
        {
            ImGui.Text(
                "La lista importata non contiene Requirement.");

            return;
        }

        var indexItems = plugin.InventoryIndex.Items;

        if (indexItems.Count == 0)
        {
            ImGui.Text(
                "Inventory Index vuoto. Nessun Requirement può essere risolto.");

            return;
        }

        var sources = indexItems
            .Select(x => new InventorySource(
                x.Storage,
                x.OwnerId,
                x.Container))
            .Distinct()
            .ToList();

        if (sources.Count == 0)
        {
            ImGui.Text(
                "Nessuna InventorySource disponibile nell'Index.");

            return;
        }

        var sourceCatalog =
            new InventorySourceCatalog();

        foreach (var source in sources)
        {
            sourceCatalog.Add(
                new SourcePolicy(
                    source,
                    Read: true,
                    Use: true));
        }

        var currentCharacterId =
            Plugin.PlayerState.IsLoaded
                ? Plugin.PlayerState.ContentId
                : 0;

        var resolutionPolicy =
            new ResolutionPolicy(
                sources,
                currentCharacterId);

        var resolver =
            new RequirementResolver();

        ImGui.Text(
            $"Source disponibili: {sources.Count}");

        ImGui.Text(
            $"Requirement da risolvere: {importedRequirementSet.Requirements.Count}");

        ImGui.Spacing();

        ImGui.Text("ORDINE PRIORITÀ");

        for (var i = 0;
             i < resolutionPolicy.Sources.Count;
             i++)
        {
            ImGui.Text(
                $"{i + 1}. {GetSourceName(resolutionPolicy.Sources[i])}");
        }

        ImGui.Spacing();

        foreach (var requirement in importedRequirementSet.Requirements)
        {
            var resolution =
                resolver.Resolve(
                    requirement,
                    plugin.InventoryIndex,
                    sourceCatalog,
                    resolutionPolicy);

            ImGui.Text(
                $"{GetItemName(requirement.BaseItemId)} | " +
                $"Richiesto {requirement.Quantity} | " +
                $"Disponibile {resolution.Available} | " +
                $"Mancante {resolution.Missing} | " +
                $"Quality {requirement.QualityPolicy} | " +
                $"Precraft {requirement.IsPrecraft}");

            if (resolution.Allocations.Count == 0)
            {
                ImGui.Text(
                    "  Nessuna allocation.");

                continue;
            }

            foreach (var allocation in resolution.Allocations)
            {
                ImGui.Text(
                    $"  -> {GetSourceName(allocation.Source)} | " +
                    $"{(allocation.IsHq ? "HQ" : "NQ")} | " +
                    $"Qty {allocation.Quantity}");
            }

            ImGui.Spacing();
        }
    }

    private void DrawIndexSearch()
    {
        ImGui.InputInt(
            "Base Item ID",
            ref searchItemId);

        if (searchItemId <= 0)
            return;

        var baseItemId = (uint)searchItemId;

        var totalQuantity =
            plugin.InventoryIndex.GetTotalQuantity(baseItemId);

        var nqQuantity =
            plugin.InventoryIndex.GetNqQuantity(baseItemId);

        var hqQuantity =
            plugin.InventoryIndex.GetHqQuantity(baseItemId);

        ImGui.Text(
            $"Item: {GetItemName(baseItemId)}");

        ImGui.Text(
            $"Base Item ID: {baseItemId}");

        ImGui.Text(
            $"Quantità totale: {totalQuantity}");

        ImGui.Text(
            $"NQ: {nqQuantity}");

        ImGui.Text(
            $"HQ: {hqQuantity}");
    }

    private void DrawStorageIndexDiagnostics()
    {
        ImGui.Text("STORAGE INDEX");
        ImGui.Separator();

        var characterCount =
            plugin.InventoryIndex.Items.Count(
                x => x.Storage == StorageType.CharacterInventory);

        var retainerCount =
            plugin.InventoryIndex.Items.Count(
                x => x.Storage == StorageType.Retainer);

        var freeCompanyCount =
            plugin.InventoryIndex.Items.Count(
                x => x.Storage == StorageType.FreeCompanyChest);

        ImGui.Text(
            $"Character Inventory: {characterCount} snapshot");

        ImGui.Text(
            $"Retainer: {retainerCount} snapshot");

        ImGui.Text(
            $"Free Company Chest: {freeCompanyCount} snapshot");

        ImGui.Text(
            $"Totale Index: {plugin.InventoryIndex.Items.Count} snapshot");

        if (retainerCount > 0)
        {
            var retainerOwners =
                plugin.InventoryIndex.Items
                    .Where(x => x.Storage == StorageType.Retainer)
                    .Select(x => x.OwnerId)
                    .Distinct()
                    .Count();

            ImGui.Text(
                $"Retainer presenti nell'Index: {retainerOwners}");
        }

        if (freeCompanyCount > 0)
        {
            var freeCompanies =
                plugin.InventoryIndex.Items
                    .Where(x => x.Storage == StorageType.FreeCompanyChest)
                    .Select(x => x.OwnerId)
                    .Distinct()
                    .Count();

            ImGui.Text(
                $"Free Company presenti nell'Index: {freeCompanies}");
        }
    }

    private void DrawSyncDiagnostics()
    {
        ImGui.Text("DEBUG SYNC RAM");
        ImGui.Separator();

        if (plugin.LastSyncAtUtc == null)
        {
            ImGui.Text(
                "Nessuna sincronizzazione eseguita.");

            return;
        }

        ImGui.Text(
            $"Ultimo sync UTC: {plugin.LastSyncAtUtc:HH:mm:ss.fff}");

        ImGui.Text(
            $"Character ID: {plugin.LastSyncCharacterId}");

        ImGui.Spacing();

        ImGui.Text(
            "DATI LETTI DA GAME INVENTORY");

        if (searchItemId > 0)
        {
            var baseItemId = (uint)searchItemId;

            var liveSnapshots =
                plugin.LastSyncSnapshots
                    .Where(x =>
                        x.BaseItemId == baseItemId &&
                        x.OwnerId == plugin.LastSyncCharacterId)
                    .OrderBy(x => x.Container)
                    .ThenBy(x => x.Slot)
                    .ToList();

            if (liveSnapshots.Count == 0)
            {
                ImGui.Text(
                    "Nessuna entry trovata per questo Base Item ID.");
            }
            else
            {
                foreach (var snapshot in liveSnapshots)
                {
                    ImGui.Text(
                        $"{GetItemName(snapshot.BaseItemId)} | " +
                        $"{GetContainerName(snapshot)} | " +
                        $"Slot {snapshot.Slot} | " +
                        $"Qty {snapshot.Quantity} | " +
                        $"{(snapshot.IsHq ? "HQ" : "NQ")} | " +
                        $"Raw {snapshot.RawItemId}");
                }
            }
        }
        else
        {
            ImGui.Text(
                "Inserisci il Base Item ID nella ricerca per vedere i dati dello sync.");
        }

        ImGui.Spacing();

        ImGui.Text(
            "DATI PRESENTI NELL'INVENTORY INDEX IN RAM");

        if (searchItemId > 0)
        {
            var baseItemId = (uint)searchItemId;

            var indexSnapshots =
                plugin.InventoryIndex.Items
                    .Where(x => x.BaseItemId == baseItemId)
                    .OrderBy(x => x.Storage)
                    .ThenBy(x => x.OwnerId)
                    .ThenBy(x => x.Container)
                    .ThenBy(x => x.Slot)
                    .ToList();

            if (indexSnapshots.Count == 0)
            {
                ImGui.Text(
                    "Nessuna entry trovata nell'indice.");
            }
            else
            {
                foreach (var snapshot in indexSnapshots)
                {
                    ImGui.Text(
                        $"{GetItemName(snapshot.BaseItemId)} | " +
                        $"{GetSourceName(new InventorySource(
                            snapshot.Storage,
                            snapshot.OwnerId,
                            snapshot.Container))} | " +
                        $"{GetContainerName(snapshot)} | " +
                        $"Slot {snapshot.Slot} | " +
                        $"Qty {snapshot.Quantity} | " +
                        $"{(snapshot.IsHq ? "HQ" : "NQ")} | " +
                        $"Raw {snapshot.RawItemId}");
                }
            }
        }
        else
        {
            ImGui.Text(
                "Inserisci il Base Item ID nella ricerca per vedere l'indice.");
        }
    }

    private static void DrawBuildInfo()
    {
        var assemblyPath =
            Plugin.PluginInterface.AssemblyLocation.FullName;

        if (!string.IsNullOrWhiteSpace(assemblyPath) &&
            File.Exists(assemblyPath))
        {
            var lastWriteTime =
                File.GetLastWriteTime(assemblyPath);

            ImGui.Text(
                $"DLL aggiornata: {lastWriteTime:dd/MM/yyyy HH:mm:ss}");

            ImGui.Text(
                $"DLL: {assemblyPath}");
        }
        else
        {
            ImGui.Text(
                "DLL aggiornata: percorso non disponibile.");
        }

        var version =
            typeof(Plugin).Assembly.GetName().Version;

        if (version != null)
        {
            ImGui.Text(
                $"Versione assembly: {version}");
        }
    }

    private static void DrawInventoryContainer(
        string containerName,
        GameInventoryType inventoryType)
    {
        var inventoryItems =
            Plugin.GameInventory.GetInventoryItems(inventoryType);

        ImGui.Text(
            $"{containerName} - Slot letti: {inventoryItems.Length}");

        var shown = 0;

        foreach (var item in inventoryItems)
        {
            if (item.ItemId == 0)
                continue;

            ImGui.Text(
                $"{GetItemName(item.BaseItemId)} | " +
                $"Base {item.BaseItemId} | " +
                $"Raw {item.ItemId} x{item.Quantity}" +
                (item.IsHq ? " [HQ]" : " [NQ]"));

            shown++;
        }

        if (shown == 0)
        {
            ImGui.Text("Vuoto.");
        }

        ImGui.Spacing();
    }

    private static string GetItemName(
        uint baseItemId)
    {
        if (baseItemId == 0)
            return "Unknown Item";

        var sheet =
            Plugin.DataManager.GetExcelSheet<Item>();

        if (sheet.TryGetRow(
                baseItemId,
                out var item))
        {
            var name = item.Name.ToString();

            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }

        return $"Unknown Item ({baseItemId})";
    }

    private string GetSourceName(
        InventorySource source)
    {
        return source.Storage switch
        {
            StorageType.CharacterInventory =>
                GetCharacterInventorySourceName(source),

            StorageType.Retainer =>
                GetRetainerSourceName(source),

            StorageType.FreeCompanyChest =>
                GetFreeCompanySourceName(source),

            _ =>
                source.Storage.ToString()
        };
    }

    private string GetCharacterInventorySourceName(
        InventorySource source)
    {
        var characterName =
            characterMonitor.GetCharacterNameById(
                source.OwnerId);

        if (string.IsNullOrWhiteSpace(characterName))
        {
            characterName =
                Plugin.PlayerState.IsLoaded
                    ? Plugin.PlayerState.CharacterName
                    : "Personaggio";
        }

        return characterName;
    }

    private string GetRetainerSourceName(
        InventorySource source)
    {
        var retainerName =
            characterMonitor.GetCharacterNameById(
                source.OwnerId);

        if (string.IsNullOrWhiteSpace(retainerName))
            retainerName = $"Retainer {source.OwnerId}";

        var parentId =
            characterMonitor.GetParentCharacterById(
                source.OwnerId)?.CharacterId ?? 0;

        if (parentId != 0)
        {
            var parentName =
                characterMonitor.GetCharacterNameById(parentId);

            if (!string.IsNullOrWhiteSpace(parentName))
            {
                return $"{retainerName} ({parentName})";
            }
        }

        return retainerName;
    }

    private string GetFreeCompanySourceName(
        InventorySource source)
    {
        var freeCompanyName =
            characterMonitor.GetCharacterNameById(
                source.OwnerId);

        if (string.IsNullOrWhiteSpace(freeCompanyName))
        {
            var freeCompany =
                characterMonitor.GetCharacterById(
                    source.OwnerId);

            freeCompanyName =
                freeCompany?.Name.ToString();
        }

        if (string.IsNullOrWhiteSpace(freeCompanyName))
        {
            freeCompanyName =
                $"Free Company {source.OwnerId}";
        }

        return freeCompanyName;
    }

    private static string GetContainerName(
        InventoryItemSnapshot snapshot)
    {
        return snapshot.Storage switch
        {
            StorageType.CharacterInventory =>
                GetCharacterContainerName(snapshot.Container),

            StorageType.Retainer =>
                GetRetainerContainerName(snapshot.Container),

            StorageType.FreeCompanyChest =>
                GetFreeCompanyContainerName(snapshot.Container),

            _ =>
                $"Container {snapshot.Container}"
        };
    }

    private static string GetCharacterContainerName(
        uint container)
    {
        return container switch
        {
            (uint)GameInventoryType.Inventory1 =>
                "Inventory 1",

            (uint)GameInventoryType.Inventory2 =>
                "Inventory 2",

            (uint)GameInventoryType.Inventory3 =>
                "Inventory 3",

            (uint)GameInventoryType.Inventory4 =>
                "Inventory 4",

            _ =>
                $"Inventory ({container})"
        };
    }

    private static string GetRetainerContainerName(
        uint container)
    {
        return container switch
        {
            10001 => "Retainer Page 1",
            10002 => "Retainer Page 2",
            10003 => "Retainer Page 3",
            10004 => "Retainer Page 4",
            10005 => "Retainer Page 5",
            10006 => "Retainer Page 6",
            10007 => "Retainer Page 7",

            _ =>
                $"Retainer Container ({container})"
        };
    }

    private static string GetFreeCompanyContainerName(
        uint container)
    {
        return container switch
        {
            20001 => "FC Chest Page 1",
            20002 => "FC Chest Page 2",
            20003 => "FC Chest Page 3",
            20004 => "FC Chest Page 4",
            20005 => "FC Chest Page 5",

            _ =>
                $"FC Chest Container ({container})"
        };
    }
}
