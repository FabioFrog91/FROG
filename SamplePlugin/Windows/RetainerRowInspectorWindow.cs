using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace FROG.Windows;

/// <summary>
/// Temporary, read-only ATK inspector for the Summoning Bell retainer list.
/// It never mutates node state and never invokes callbacks or game actions.
/// </summary>
public sealed unsafe class RetainerRowInspectorWindow : Window, IDisposable
{
    private const uint ListComponentNodeId = 27;

    private string snapshotText =
        "Apri la Summoning Bell / Retainer List, poi premi AGGIORNA LETTURA.";

    public RetainerRowInspectorWindow()
        : base("FROG - Retainer Row Inspector###FROGRetainerRowInspector")
    {
        SizeConstraints =
            new WindowSizeConstraints
            {
                MinimumSize = new Vector2(560, 360),
                MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
            };
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        ImGui.TextWrapped(
            "DEBUG TEMPORANEO READ-ONLY: legge il tree ATK delle righe RetainerList. " +
            "Non modifica colori, testo, callback o stato di gioco.");

        ImGui.Spacing();

        if (ImGui.Button("AGGIORNA LETTURA"))
        {
            snapshotText =
                BuildSnapshot();
        }

        ImGui.SameLine();

        if (ImGui.Button("COPIA OUTPUT"))
        {
            ImGui.SetClipboardText(
                snapshotText);
        }

        ImGui.Separator();

        ImGui.BeginChild(
            "RetainerRowInspectorOutput",
            Vector2.Zero,
            true);

        ImGui.TextUnformatted(
            snapshotText);

        ImGui.EndChild();
    }

    private static string BuildSnapshot()
    {
        var lines =
            new List<string>
            {
                "FROG DEBUG | RETAINER ROW ATK INSPECTOR",
                $"GeneratedUtc={DateTime.UtcNow:O}",
                "Mode=READ_ONLY"
            };

        var addonAddress =
            Plugin.GameGui.GetAddonByName(
                "RetainerList",
                1);

        if (addonAddress == IntPtr.Zero)
        {
            lines.Add("RetainerList=NOT_OPEN");
            return string.Join(
                Environment.NewLine,
                lines);
        }

        var addon =
            (AtkUnitBase*)addonAddress.Address;

        if (addon == null)
        {
            lines.Add("RetainerList=INVALID_POINTER");
            return string.Join(
                Environment.NewLine,
                lines);
        }

        lines.Add(
            $"RetainerList.NodeListCount={addon->UldManager.NodeListCount}");

        var listNode =
            (AtkComponentNode*)addon->GetNodeById(
                ListComponentNodeId);

        if (listNode == null ||
            (ushort)listNode->AtkResNode.Type < 1000 ||
            listNode->Component == null)
        {
            lines.Add(
                $"ListComponent[{ListComponentNodeId}]=NOT_AVAILABLE");

            return string.Join(
                Environment.NewLine,
                lines);
        }

        AppendNode(
            lines,
            "LIST",
            &listNode->AtkResNode,
            -1);

        var manager =
            RetainerManager.Instance();

        if (manager == null)
        {
            lines.Add("RetainerManager=NOT_AVAILABLE");
            return string.Join(
                Environment.NewLine,
                lines);
        }

        var count =
            manager->GetRetainerCount();

        lines.Add(
            $"RetainerCount={count}");

        for (uint i = 0;
             i < count;
             i++)
        {
            var retainer =
                manager->GetRetainerBySortedIndex(i);

            if (retainer == null)
                continue;

            var rendererNodeId =
                i == 0
                    ? 4U
                    : 41000U + i;

            var renderer =
                FindNodeById(
                    listNode->Component->UldManager,
                    rendererNodeId);

            lines.Add(string.Empty);
            lines.Add(
                $"ROW[{i}] RetainerId={retainer->RetainerId} Name={retainer->NameString} RendererNodeId={rendererNodeId}");

            if (renderer == null)
            {
                lines.Add("  Renderer=NOT_FOUND");
                continue;
            }

            AppendNode(
                lines,
                "  RENDERER",
                renderer,
                -1);

            if ((ushort)renderer->Type < 1000)
            {
                lines.Add(
                    "  Renderer is not a component node.");

                continue;
            }

            var componentNode =
                (AtkComponentNode*)renderer;

            if (componentNode->Component == null)
            {
                lines.Add(
                    "  Renderer.Component=NULL");

                continue;
            }

            var childManager =
                componentNode->Component->UldManager;

            lines.Add(
                $"  ChildNodeListCount={childManager.NodeListCount}");

            for (var childIndex = 0;
                 childIndex < childManager.NodeListCount;
                 childIndex++)
            {
                var child =
                    childManager.NodeList[childIndex];

                AppendNode(
                    lines,
                    "    CHILD",
                    child,
                    childIndex);
            }
        }

        return string.Join(
            Environment.NewLine,
            lines);
    }

    private static AtkResNode* FindNodeById(
        AtkUldManager manager,
        uint nodeId)
    {
        for (var i = 0;
             i < manager.NodeListCount;
             i++)
        {
            var node =
                manager.NodeList[i];

            if (node != null &&
                node->NodeId == nodeId)
            {
                return node;
            }
        }

        return null;
    }

    private static void AppendNode(
        List<string> lines,
        string prefix,
        AtkResNode* node,
        int index)
    {
        if (node == null)
        {
            lines.Add(
                $"{prefix}[{index}]=NULL");

            return;
        }

        lines.Add(
            $"{prefix}[{index}] " +
            $"NodeId={node->NodeId} " +
            $"Type={(ushort)node->Type}({node->Type}) " +
            $"Visible={node->IsVisible()} " +
            $"X={node->X} Y={node->Y} " +
            $"W={node->Width} H={node->Height} " +
            $"ScaleX={node->ScaleX:F3} ScaleY={node->ScaleY:F3}");
    }
}
