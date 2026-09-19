using CriticalCommonLib.Models;
using CriticalCommonLib.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Inventory;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using FROG.Core.Inventory;
using FROG.Core.Inventory.Providers;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

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

        if (ImGui.CollapsingHeader(
                "PERSONAGGIO",
                ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Text($"Nome: {playerState.CharacterName}");
            ImGui.Text($"Livello: {playerState.Level}");

            if (playerState.ClassJob.IsValid)
            {
                ImGui.Text(
                    $"Job: {playerState.ClassJob.Value.Abbreviation}");
            }

            ImGui.Text($"Content ID: {playerState.ContentId:X}");
        }

        ImGui.Spacing();

        DrawTeamcraftImport();

        ImGui.Spacing();

        DrawResolverDiagnostics();

        ImGui.Spacing();

        if (ImGui.CollapsingHeader("CLIENT"))
        {
            DrawClientDiagnostics();
        }

        ImGui.Spacing();

        if (ImGui.CollapsingHeader("INVENTARIO"))
        {
            DrawInventoryDiagnostics();
        }

        ImGui.Spacing();

        if (ImGui.CollapsingHeader("STORAGE INDEX"))
        {
            DrawStorageIndexDiagnostics();
        }

        ImGui.Spacing();

        if (ImGui.CollapsingHeader("DEBUG SYNC RAM"))
        {
            DrawSyncDiagnostics();
        }

        ImGui.Spacing();

        if (ImGui.CollapsingHeader("RICERCA NELL'INDICE"))
        {
            DrawIndexSearch();
        }
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
        ImGui.Text("RESOLVER / TRANSFER PLAN");
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
            .Select(CreateSource)
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

        var planner =
            new TransferPlanner(
                new RequirementResolver());

        var plan =
            planner.Plan(
                importedRequirementSet,
                plugin.InventoryIndex,
                sourceCatalog,
                resolutionPolicy);

        ImGui.Text(
            $"Source disponibili: {sources.Count}");

        ImGui.Text(
            $"Requirement: {importedRequirementSet.Requirements.Count}");

        ImGui.Text(
            $"Intent: {plan.Intents.Count}");

        ImGui.Text(
            $"Mancante: {plan.Missing}");

        ImGui.Text(
            $"Piano completo: {(plan.IsComplete ? "SI" : "NO")}");

        ImGui.Spacing();

        if (ImGui.CollapsingHeader(
                "ORDINE PRIORITÀ",
                ImGuiTreeNodeFlags.DefaultOpen))
        {
            for (var i = 0;
                 i < resolutionPolicy.Sources.Count;
                 i++)
            {
                ImGui.Text(
                    $"{i + 1}. {GetSourceName(resolutionPolicy.Sources[i])}");
            }
        }

        ImGui.Spacing();

        if (ImGui.CollapsingHeader(
                "RESOLUTION",
                ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (var resolution in plan.Resolutions)
            {
                ImGui.Text(
                    $"{GetItemName(resolution.Requirement.BaseItemId)} | " +
                    $"Richiesto {resolution.Requirement.Quantity} | " +
                    $"Disponibile {resolution.Available} | " +
                    $"Mancante {resolution.Missing}");

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

        ImGui.Spacing();

        if (ImGui.CollapsingHeader(
                "TRANSFER INTENTS",
                ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (plan.Intents.Count == 0)
            {
                ImGui.Text(
                    "Nessun TransferIntent generato.");
            }
            else
            {
                foreach (var intent in plan.Intents)
                {
                    ImGui.Text(
                        $"{GetItemName(intent.BaseItemId)} | " +
                        $"{GetSourceName(intent.Source)} | " +
                        $"{(intent.IsHq ? "HQ" : "NQ")} | " +
                        $"Qty {intent.Quantity}");
                }
            }
        }
    }

    private void DrawClientDiagnostics()
    {
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
    }

    private void DrawInventoryDiagnostics()
    {
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
        ImGui.Text(
            $"Character Inventory: " +
            $"{plugin.InventoryIndex.Items.Count(x => x.Storage == StorageType.CharacterInventory)} snapshot");

        ImGui.Text(
            $"Retainer: " +
            $"{plugin.InventoryIndex.Items.Count(x => x.Storage == StorageType.Retainer)} snapshot");

        ImGui.Text(
            $"Free Company Chest: " +
            $"{plugin.InventoryIndex.Items.Count(x => x.Storage == StorageType.FreeCompanyChest)} snapshot");

        ImGui.Text(
            $"Totale Index: {plugin.InventoryIndex.Items.Count} snapshot");

        var retainerOwners =
            plugin.InventoryIndex.Items
                .Where(x => x.Storage == StorageType.Retainer)
                .Select(x => x.OwnerId)
                .Distinct()
                .Count();

        if (retainerOwners > 0)
        {
            ImGui.Text(
                $"Retainer presenti nell'Index: {retainerOwners}");
        }

        var freeCompanies =
            plugin.InventoryIndex.Items
                .Where(x => x.Storage == StorageType.FreeCompanyChest)
                .Select(x => x.OwnerId)
                .Distinct()
                .Count();

        if (freeCompanies > 0)
        {
            ImGui.Text(
                $"Free Company presenti nell'Index: {freeCompanies}");
        }
    }

    private void DrawSyncDiagnostics()
    {
        ImGui.Text("DEBUG SYNC");
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

        if (searchItemId <= 0)
        {
            ImGui.TextWrapped(
                "Inserisci il Base Item ID nella sezione \"RICERCA NELL'INDICE\" per visualizzare la tabella di debug.");

            return;
        }

        var baseItemId = (uint)searchItemId;

        ImGui.Text(
            $"DEBUG ITEM: {GetItemName(baseItemId)}");

        ImGui.Text(
            $"Base Item ID: {baseItemId}");

        ImGui.Spacing();

        if (ImGui.Button("COPIA DEBUG"))
        {
            var clipboardText =
                BuildDebugClipboardText(baseItemId);

            ImGui.SetClipboardText(clipboardText);
        }

        ImGui.SameLine();

        ImGui.Text(
            "Copia GAME INVENTORY + INDEX");

        ImGui.Spacing();

        var liveSnapshots =
            plugin.LastSyncSnapshots
                .Where(x =>
                    x.BaseItemId == baseItemId)
                .OrderBy(x => x.Container)
                .ThenBy(x => x.Slot)
                .ToList();

        var indexSnapshots =
            plugin.InventoryIndex.Items
                .Where(x =>
                    x.BaseItemId == baseItemId)
                .OrderBy(x => x.Storage)
                .ThenBy(x => x.OwnerId)
                .ThenBy(x => x.Container)
                .ThenBy(x => x.Slot)
                .ToList();

        ImGui.Text(
            $"GAME INVENTORY: {liveSnapshots.Count} snapshot");

        ImGui.SameLine();

        ImGui.Text(
            $" | INDEX: {indexSnapshots.Count} snapshot");

        ImGui.Spacing();

        DrawDebugTable(
            liveSnapshots,
            indexSnapshots);

        ImGui.Spacing();

        DrawRawRetainerDiagnostics(baseItemId);

        ImGui.Spacing();

        DrawRetainerScannerComparisonDiagnostics(baseItemId);
    }

    private void DrawRawRetainerDiagnostics(
        uint baseItemId)
    {
        ImGui.Text("CCL RAW RETAINER");
        ImGui.Separator();

        var rawItems =
            CriticalCommonLibInventoryProvider.LastRawRetainerItems
                .Where(x => x.BaseItemId == baseItemId)
                .OrderBy(x => x.ContainerType)
                .ThenBy(x => x.Slot)
                .ToList();

        if (rawItems.Count == 0)
        {
            ImGui.Text(
                "Nessun item del retainer trovato nella lettura raw CCL per questo Base Item ID.");

            return;
        }

        ImGui.Text(
            $"Item raw CCL trovati: {rawItems.Count}");

        if (!ImGui.BeginTable(
                "FrogRawRetainerTable",
                8,
                ImGuiTableFlags.Borders |
                ImGuiTableFlags.RowBg |
                ImGuiTableFlags.ScrollX |
                ImGuiTableFlags.ScrollY |
                ImGuiTableFlags.Resizable |
                ImGuiTableFlags.SizingFixedFit,
                new Vector2(0, 300)))
        {
            return;
        }

        ImGui.TableSetupColumn("Container");
        ImGui.TableSetupColumn("Container ID");
        ImGui.TableSetupColumn("Slot");
        ImGui.TableSetupColumn("Raw ID");
        ImGui.TableSetupColumn("Base ID");
        ImGui.TableSetupColumn("Qty");
        ImGui.TableSetupColumn("HQ");
        ImGui.TableSetupColumn("Retainer ID");

        ImGui.TableHeadersRow();

        foreach (var item in rawItems)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(
                item.ContainerType.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(
                ((uint)item.ContainerType).ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(
                item.Slot.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(
                item.RawItemId.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(
                item.BaseItemId.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(
                item.Quantity.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(
                item.IsHq ? "HQ" : "NQ");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(
                item.RetainerId.ToString());
        }

        ImGui.EndTable();
    }

    private void DrawRetainerScannerComparisonDiagnostics(
        uint baseItemId)
    {
        ImGui.Text("CCL SCANNER COMPARISON");
        ImGui.Separator();

        var comparisons =
            CriticalCommonLibInventoryProvider.LastRetainerScannerComparisons
                .Where(x =>
                    x.GetInventoryRawItemId != 0 &&
                    GetBaseItemId(x.GetInventoryRawItemId) == baseItemId ||
                    x.RetainerBagRawItemId != 0 &&
                    GetBaseItemId(x.RetainerBagRawItemId) == baseItemId)
                .OrderBy(x => x.ContainerType)
                .ThenBy(x => x.Slot)
                .ToList();

        if (comparisons.Count == 0)
        {
            ImGui.Text(
                "Nessun confronto CCL trovato per questo Base Item ID.");

            return;
        }

        ImGui.Text(
            $"Confronti trovati: {comparisons.Count}");

        if (!ImGui.BeginTable(
                "FrogRetainerScannerComparisonTable",
                10,
                ImGuiTableFlags.Borders |
                ImGuiTableFlags.RowBg |
                ImGuiTableFlags.ScrollX |
                ImGuiTableFlags.ScrollY |
                ImGuiTableFlags.Resizable |
                ImGuiTableFlags.SizingFixedFit,
                new Vector2(0, 300)))
        {
            return;
        }

        ImGui.TableSetupColumn("Container");
        ImGui.TableSetupColumn("Slot");
        ImGui.TableSetupColumn("GetInventory Raw");
        ImGui.TableSetupColumn("GetInventory Qty");
        ImGui.TableSetupColumn("RetainerBag Raw");
        ImGui.TableSetupColumn("RetainerBag Qty");
        ImGui.TableSetupColumn("Same Raw");
        ImGui.TableSetupColumn("Same Qty");
        ImGui.TableSetupColumn("Retainer ID");
        ImGui.TableSetupColumn("Esito");

        ImGui.TableHeadersRow();

        foreach (var comparison in comparisons)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(
                comparison.ContainerType.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(
                comparison.Slot.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(
                comparison.GetInventoryRawItemId.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(
                comparison.GetInventoryQuantity.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(
                comparison.RetainerBagRawItemId.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(
                comparison.RetainerBagQuantity.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(
                comparison.SameItem ? "YES" : "NO");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(
                comparison.SameQuantity ? "YES" : "NO");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(
                comparison.RetainerId.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(
                comparison.SameItem &&
                comparison.SameQuantity
                    ? "UGUALE"
                    : "DIVERSO");
        }

        ImGui.EndTable();
    }

    private static uint GetBaseItemId(
        uint rawItemId)
    {
        if (rawItemId == 0)
            return 0;

        var baseItem =
            ItemUtil.GetBaseId(rawItemId);

        return baseItem.ItemId;
    }

    private void DrawDebugTable(
        IReadOnlyList<InventoryItemSnapshot> liveSnapshots,
        IReadOnlyList<InventoryItemSnapshot> indexSnapshots)
    {
        if (!ImGui.BeginTable(
                "FrogDebugTable",
                11,
                ImGuiTableFlags.Borders |
                ImGuiTableFlags.RowBg |
                ImGuiTableFlags.ScrollX |
                ImGuiTableFlags.ScrollY |
                ImGuiTableFlags.Resizable |
                ImGuiTableFlags.SizingFixedFit,
                new Vector2(0, 350)))
        {
            return;
        }

        ImGui.TableSetupColumn("Fonte");
        ImGui.TableSetupColumn("Storage");
        ImGui.TableSetupColumn("Owner");
        ImGui.TableSetupColumn("Owner ID");
        ImGui.TableSetupColumn("Container");
        ImGui.TableSetupColumn("Slot");
        ImGui.TableSetupColumn("Raw ID");
        ImGui.TableSetupColumn("Base ID");
        ImGui.TableSetupColumn("Qty");
        ImGui.TableSetupColumn("HQ");
        ImGui.TableSetupColumn("Verified");

        ImGui.TableHeadersRow();

        foreach (var snapshot in liveSnapshots)
        {
            DrawDebugRow(
                "GAME INVENTORY",
                snapshot);
        }

        foreach (var snapshot in indexSnapshots)
        {
            DrawDebugRow(
                "INDEX",
                snapshot);
        }

        ImGui.EndTable();
    }

    private void DrawDebugRow(
        string sourceName,
        InventoryItemSnapshot snapshot)
    {
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(sourceName);

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(snapshot.Storage.ToString());

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(GetOwnerName(snapshot));

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(snapshot.OwnerId.ToString());

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(GetContainerName(snapshot));

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(snapshot.Slot.ToString());

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(snapshot.RawItemId.ToString());

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(snapshot.BaseItemId.ToString());

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(snapshot.Quantity.ToString());

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(
            snapshot.IsHq ? "HQ" : "NQ");

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(
            snapshot.IsVerified ? "YES" : "NO");
    }

    private string BuildDebugClipboardText(
        uint baseItemId)
    {
        var liveSnapshots =
            plugin.LastSyncSnapshots
                .Where(x =>
                    x.BaseItemId == baseItemId)
                .OrderBy(x => x.Container)
                .ThenBy(x => x.Slot)
                .ToList();

        var indexSnapshots =
            plugin.InventoryIndex.Items
                .Where(x =>
                    x.BaseItemId == baseItemId)
                .OrderBy(x => x.Storage)
                .ThenBy(x => x.OwnerId)
                .ThenBy(x => x.Container)
                .ThenBy(x => x.Slot)
                .ToList();

        var lines =
            new List<string>
            {
                $"FROG DEBUG | Item={GetItemName(baseItemId)} | BaseItemId={baseItemId}",
                $"GeneratedUtc={DateTime.UtcNow:O}",
                $"LastSyncUtc={plugin.LastSyncAtUtc:O}",
                $"CharacterId={plugin.LastSyncCharacterId}",
                string.Empty,
                "SOURCE\tSTORAGE\tOWNER\tOWNER_ID\tCONTAINER\tSLOT\tRAW_ID\tBASE_ID\tQTY\tHQ\tVERIFIED"
            };

        foreach (var snapshot in liveSnapshots)
        {
            lines.Add(
                BuildDebugClipboardRow(
                    "GAME INVENTORY",
                    snapshot));
        }

        foreach (var snapshot in indexSnapshots)
        {
            lines.Add(
                BuildDebugClipboardRow(
                    "INDEX",
                    snapshot));
        }

        return string.Join(
            Environment.NewLine,
            lines);
    }

    private string BuildDebugClipboardRow(
        string sourceName,
        InventoryItemSnapshot snapshot)
    {
        return string.Join(
            "\t",
            sourceName,
            snapshot.Storage,
            GetOwnerName(snapshot),
            snapshot.OwnerId,
            GetContainerName(snapshot),
            snapshot.Slot,
            snapshot.RawItemId,
            snapshot.BaseItemId,
            snapshot.Quantity,
            snapshot.IsHq ? "HQ" : "NQ",
            snapshot.IsVerified ? "YES" : "NO");
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

    private InventorySource CreateSource(
        InventoryItemSnapshot snapshot)
    {
        var parentCharacterId =
            snapshot.Storage == StorageType.Retainer
                ? characterMonitor.GetParentCharacterById(
                    snapshot.OwnerId)?.CharacterId ?? 0
                : snapshot.Storage == StorageType.FreeCompanyChest
                    ? characterMonitor.GetCharacterById(
                        snapshot.OwnerId)?.CharacterId ?? 0
                    : 0;

        return new InventorySource(
            snapshot.Storage,
            snapshot.OwnerId,
            snapshot.Container,
            parentCharacterId);
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

    private string GetOwnerName(
        InventoryItemSnapshot snapshot)
    {
        return snapshot.Storage switch
        {
            StorageType.CharacterInventory =>
                GetCharacterInventoryOwnerName(snapshot.OwnerId),

            StorageType.Retainer =>
                GetRetainerOwnerName(snapshot.OwnerId),

            StorageType.FreeCompanyChest =>
                GetFreeCompanyOwnerName(snapshot.OwnerId),

            _ =>
                snapshot.OwnerId.ToString()
        };
    }

    private string GetCharacterInventoryOwnerName(
        ulong ownerId)
    {
        var characterName =
            characterMonitor.GetCharacterNameById(ownerId);

        if (!string.IsNullOrWhiteSpace(characterName))
            return characterName;

        if (Plugin.PlayerState.IsLoaded &&
            Plugin.PlayerState.ContentId == ownerId)
        {
            return Plugin.PlayerState.CharacterName;
        }

        return $"Character {ownerId}";
    }

    private string GetRetainerOwnerName(
        ulong ownerId)
    {
        var retainerName =
            characterMonitor.GetCharacterNameById(ownerId);

        if (!string.IsNullOrWhiteSpace(retainerName))
            return retainerName;

        return $"Retainer {ownerId}";
    }

    private string GetFreeCompanyOwnerName(
        ulong ownerId)
    {
        var freeCompanyName =
            characterMonitor.GetCharacterNameById(ownerId);

        if (!string.IsNullOrWhiteSpace(freeCompanyName))
            return freeCompanyName;

        var freeCompany =
            characterMonitor.GetCharacterById(ownerId);

        var name =
            freeCompany?.Name.ToString();

        if (!string.IsNullOrWhiteSpace(name))
            return name;

        return $"Free Company {ownerId}";
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
