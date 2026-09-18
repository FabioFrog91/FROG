using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Inventory;
using Dalamud.Interface.Windowing;
using FROG.Core.Inventory;
using Lumina.Excel.Sheets;

namespace FROG.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private RequirementSet? importedRequirementSet;

    public MainWindow(Plugin plugin)
        : base("FROG")
    {
        this.plugin = plugin;

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

        ImGui.Text("CLIENT");
        ImGui.Separator();

        var territoryId = Plugin.ClientState.TerritoryType;

        ImGui.Text($"Territory ID: {territoryId}");

        if (Plugin.DataManager
            .GetExcelSheet<TerritoryType>()
            .TryGetRow(territoryId, out var territoryRow))
        {
            ImGui.Text($"Zona: {territoryRow.PlaceName.Value.Name}");
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
                new TeamcraftListImporter(Plugin.DataManager);

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
                $"Base {requirement.BaseItemId} | " +
                $"Qty {requirement.Quantity} | " +
                $"Quality {quality} | " +
                $"Precraft {requirement.IsPrecraft}");
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

        ImGui.Text($"Base Item ID: {baseItemId}");
        ImGui.Text($"Quantità totale: {totalQuantity}");
        ImGui.Text($"NQ: {nqQuantity}");
        ImGui.Text($"HQ: {hqQuantity}");
    }

    private int searchItemId;

    private void DrawStorageIndexDiagnostics()
    {
        ImGui.Text("STORAGE INDEX");
        ImGui.Separator();

        var characterCount = plugin.InventoryIndex.Items.Count(x =>
            x.Storage == StorageType.CharacterInventory);

        var retainerCount = plugin.InventoryIndex.Items.Count(x =>
            x.Storage == StorageType.Retainer);

        var freeCompanyCount = plugin.InventoryIndex.Items.Count(x =>
            x.Storage == StorageType.FreeCompanyChest);

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
            var retainerOwners = plugin.InventoryIndex.Items
                .Where(x => x.Storage == StorageType.Retainer)
                .Select(x => x.OwnerId)
                .Distinct()
                .Count();

            ImGui.Text(
                $"Retainer presenti nell'Index: {retainerOwners}");
        }

        if (freeCompanyCount > 0)
        {
            var freeCompanies = plugin.InventoryIndex.Items
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
            ImGui.Text("Nessuna sincronizzazione eseguita.");
            return;
        }

        ImGui.Text(
            $"Ultimo sync UTC: {plugin.LastSyncAtUtc:HH:mm:ss.fff}");

        ImGui.Text(
            $"Character ID: {plugin.LastSyncCharacterId}");

        ImGui.Spacing();

        ImGui.Text("DATI LETTI DA GAME INVENTORY");

        if (searchItemId > 0)
        {
            var baseItemId = (uint)searchItemId;

            var liveSnapshots = plugin.LastSyncSnapshots
                .Where(x =>
                    x.BaseItemId == baseItemId &&
                    x.OwnerId == plugin.LastSyncCharacterId)
                .OrderBy(x => x.Container)
                .ThenBy(x => x.Slot)
                .ToList();

            if (liveSnapshots.Count == 0)
            {
                ImGui.Text("Nessuna entry trovata per questo Base Item ID.");
            }
            else
            {
                foreach (var snapshot in liveSnapshots)
                {
                    ImGui.Text(
                        $"Container {snapshot.Container} | " +
                        $"Slot {snapshot.Slot} | " +
                        $"Qty {snapshot.Quantity} | " +
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

        ImGui.Text("DATI PRESENTI NELL'INVENTORY INDEX IN RAM");

        if (searchItemId > 0)
        {
            var baseItemId = (uint)searchItemId;

            var indexSnapshots = plugin.LastSyncIndexSnapshots
                .Where(x => x.BaseItemId == baseItemId)
                .OrderBy(x => x.Container)
                .ThenBy(x => x.Slot)
                .ToList();

            if (indexSnapshots.Count == 0)
            {
                ImGui.Text("Nessuna entry trovata nell'indice.");
            }
            else
            {
                foreach (var snapshot in indexSnapshots)
                {
                    ImGui.Text(
                        $"Container {snapshot.Container} | " +
                        $"Slot {snapshot.Slot} | " +
                        $"Qty {snapshot.Quantity} | " +
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
        var assemblyPath = Plugin.PluginInterface.AssemblyLocation.FullName;

        if (!string.IsNullOrWhiteSpace(assemblyPath) &&
            File.Exists(assemblyPath))
        {
            var lastWriteTime = File.GetLastWriteTime(assemblyPath);

            ImGui.Text(
                $"DLL aggiornata: {lastWriteTime:dd/MM/yyyy HH:mm:ss}");

            ImGui.Text($"DLL: {assemblyPath}");
        }
        else
        {
            ImGui.Text("DLL aggiornata: percorso non disponibile.");
        }

        var version = typeof(Plugin).Assembly.GetName().Version;

        if (version != null)
        {
            ImGui.Text($"Versione assembly: {version}");
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
                $"Base {item.BaseItemId} | Raw {item.ItemId} x{item.Quantity}" +
                (item.IsHq ? " [HQ]" : " [NQ]"));

            shown++;
        }

        if (shown == 0)
        {
            ImGui.Text("Vuoto.");
        }

        ImGui.Spacing();
    }
}
