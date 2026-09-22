using Dalamud.Bindings.ImGui;
using FROG.Core.Inventory;
using System;
using System.Collections.Generic;

namespace FROG.Windows;

internal static class FreeCompanyObservationDiagnosticsPanel
{
    public static void Draw()
    {
        var entries =
            FreeCompanyObservationDiagnostics.Snapshot();

        ImGui.Text($"Eventi in RAM: {entries.Count}");

        if (ImGui.Button("COPIA DEBUG FC"))
        {
            ImGui.SetClipboardText(
                BuildClipboardText(entries));
        }

        ImGui.SameLine();

        if (ImGui.Button("SVUOTA DEBUG FC"))
        {
            FreeCompanyObservationDiagnostics.Clear();
        }

        ImGui.Spacing();

        if (entries.Count == 0)
        {
            ImGui.TextWrapped(
                "Nessun evento FC registrato in RAM.");

            return;
        }

        var childVisible =
            ImGui.BeginChild(
                "FrogFreeCompanyObservationDiagnostics",
                new System.Numerics.Vector2(0, 300),
                true);

        if (childVisible)
        {
            foreach (var entry in entries)
            {
                ImGui.TextUnformatted(entry);
            }
        }

        ImGui.EndChild();
    }

    private static string BuildClipboardText(
        IReadOnlyList<string> entries)
    {
        var lines =
            new List<string>
            {
                "FROG DEBUG | FREE COMPANY OBSERVATION",
                $"GeneratedUtc={DateTime.UtcNow:O}",
                $"EntryCount={entries.Count}",
                string.Empty
            };

        lines.AddRange(entries);

        return string.Join(
            Environment.NewLine,
            lines);
    }
}
