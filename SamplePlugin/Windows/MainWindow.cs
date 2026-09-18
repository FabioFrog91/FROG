using System;
using System.Collections.Generic;
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
    private int searchItemId;

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

        ScanPlayerInventory();

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

        ImGui.Text($"Oggetti indicizzati: {plugin.InventoryIndex.Items.Count}");

        ImGui.Spacing();

        ImGui.Text("RICERCA NELL'INDICE");
        ImGui.Separator();

        ImGui.InputInt("Item ID", ref searchItemId);

        if (searchItemId > 0)
        {
            var itemId = (ulong)searchItemId;
            var totalQuantity =
                plugin.InventoryIndex.GetTotalQuantity(itemId);

            var nqQuantity = plugin.InventoryIndex.GetNqQuantity(itemId);
            var hqQuantity = plugin.InventoryIndex.GetHqQuantity(itemId);

            ImGui.Text($"Item ID: {itemId}");
            ImGui.Text($"Quantità totale: {totalQuantity}");
            ImGui.Text($"NQ: {nqQuantity}");
            ImGui.Text($"HQ: {hqQuantity}");
        }
    }

    private void ScanPlayerInventory()
    {
        var snapshots = new List<InventoryItemSnapshot>();

        AddInventorySnapshots(
            snapshots,
            GameInventoryType.Inventory1);

        AddInventorySnapshots(
            snapshots,
            GameInventoryType.Inventory2);

        AddInventorySnapshots(
            snapshots,
            GameInventoryType.Inventory3);

        AddInventorySnapshots(
            snapshots,
            GameInventoryType.Inventory4);

        plugin.InventoryIndex.ReplaceAll(snapshots);
    }

    private static void AddInventorySnapshots(
        List<InventoryItemSnapshot> snapshots,
        GameInventoryType inventoryType)
    {
        var inventoryItems =
            Plugin.GameInventory.GetInventoryItems(inventoryType);

        for (var slot = 0; slot < inventoryItems.Length; slot++)
        {
            var item = inventoryItems[slot];

            if (item.ItemId == 0)
                continue;

            snapshots.Add(
                new InventoryItemSnapshot(
                    item.ItemId,
                    item.Quantity,
                    item.IsHq,
                    StorageType.CharacterInventory,
                    Plugin.PlayerState.ContentId,
                    (uint)inventoryType,
                    slot));
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
                $"ItemId {item.ItemId} x{item.Quantity}" +
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
