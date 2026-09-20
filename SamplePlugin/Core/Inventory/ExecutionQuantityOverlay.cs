using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Common.Math;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Numerics;

namespace FROG.Core.Inventory;

/// <summary>
/// Draws small passive "-N" quantity badges next to the currently highlighted
/// inventory slots. This overlay never mutates game UI nodes.
/// </summary>
public sealed unsafe class ExecutionQuantityOverlay
{
    private readonly IGameGui gameGui;
    private readonly ExecutionInventoryHighlighter inventoryHighlighter;
    private readonly Plugin plugin;

    public ExecutionQuantityOverlay(
        IGameGui gameGui,
        ExecutionInventoryHighlighter inventoryHighlighter,
        Plugin plugin)
    {
        this.gameGui = gameGui;
        this.inventoryHighlighter = inventoryHighlighter;
        this.plugin = plugin;
    }

    public void Draw()
    {
        if (!plugin.Configuration.EnableExecutionQuantityOverlay)
            return;

        var anchors =
            inventoryHighlighter.QuantityBadgeAnchors;

        if (anchors.Count == 0)
            return;

        var drawList =
            ImGui.GetForegroundDrawList();

        foreach (var anchor in anchors)
        {
            var wrapper =
                gameGui.GetAddonByName(
                    anchor.AddonName,
                    1);

            if (wrapper == IntPtr.Zero ||
                wrapper.Address != anchor.AddonAddress)
            {
                continue;
            }

            var addon =
                (AtkUnitBase*)wrapper.Address;

            if (addon == null ||
                !addon->IsVisible)
            {
                continue;
            }

            var node =
                addon->GetNodeById(
                    anchor.NodeId);

            if (node == null ||
                !node->IsVisible())
            {
                continue;
            }

            Bounds bounds = default;
            node->GetBounds(
                &bounds);

            if (bounds.Width <= 0 ||
                bounds.Height <= 0)
            {
                continue;
            }

            var text =
                $"-{anchor.Quantity}";

            var textSize =
                ImGui.CalcTextSize(
                    text);

            var padding =
                new Vector2(
                    4f,
                    2f);

            var textPosition =
                new Vector2(
                    bounds.Pos2.X -
                    textSize.X -
                    padding.X,
                    bounds.Pos1.Y +
                    padding.Y);

            var backgroundMin =
                textPosition -
                padding;

            var backgroundMax =
                textPosition +
                textSize +
                padding;

            var backgroundColour =
                ImGui.ColorConvertFloat4ToU32(
                    new Vector4(
                        0f,
                        0f,
                        0f,
                        0.82f));

            var textColour =
                ImGui.ColorConvertFloat4ToU32(
                    plugin.Configuration.RetainerRowHighlightColor);

            drawList.AddRectFilled(
                backgroundMin,
                backgroundMax,
                backgroundColour,
                4f);

            drawList.AddText(
                textPosition,
                textColour,
                text);
        }
    }
}
