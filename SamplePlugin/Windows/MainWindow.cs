using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;

namespace FROG.Windows;

public class MainWindow : Window, IDisposable
{
    public MainWindow(Plugin plugin)
        : base("FROG")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(375, 200),
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

        ImGui.Text($"Livello: {playerState.Level}");

        if (playerState.ClassJob.IsValid)
        {
            ImGui.Text($"Job: {playerState.ClassJob.Value.Abbreviation}");
        }

        var territoryId = Plugin.ClientState.TerritoryType;

        if (Plugin.DataManager
            .GetExcelSheet<TerritoryType>()
            .TryGetRow(territoryId, out var territoryRow))
        {
            ImGui.Text($"Zona: {territoryRow.PlaceName.Value.Name}");
        }
    }
}
