using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;

namespace FROG.Core.Inventory;

/// <summary>
/// Highlights the row of the retainer required by the current execution action.
/// The implementation only changes presentation fields on RetainerList's native
/// full-row hover layer (child node 14). It never invokes callbacks, clicks,
/// inventory actions, or gameplay functions.
/// </summary>
public sealed unsafe class RetainerListHighlighter
{
    private const uint ListComponentNodeId = 27;
    private const uint RowHighlightNodeId = 14;

    private readonly IGameGui gameGui;
    private readonly PlanExecutionRuntime executionRuntime;
    private readonly Plugin plugin;

    private ulong highlightedRetainerId;
    private OriginalNodeState? originalState;

    public RetainerListHighlighter(
        IGameGui gameGui,
        PlanExecutionRuntime executionRuntime,
        Plugin plugin)
    {
        this.gameGui = gameGui;
        this.executionRuntime = executionRuntime;
        this.plugin = plugin;
    }

    public void Update(
        ulong currentCharacterId)
    {
        var addonAddress =
            gameGui.GetAddonByName(
                "RetainerList",
                1);

        if (addonAddress == IntPtr.Zero)
        {
            ForgetDestroyedAddon();
            return;
        }

        var targetRetainerId =
            GetTargetRetainerId(
                currentCharacterId);

        if (targetRetainerId == 0 ||
            !plugin.Configuration.EnableRetainerRowHighlight)
        {
            Clear();
            return;
        }

        if (highlightedRetainerId != 0 &&
            highlightedRetainerId != targetRetainerId)
        {
            Clear();
        }

        var node =
            FindHighlightNode(
                targetRetainerId);

        if (node == null)
        {
            Clear();
            return;
        }

        if (highlightedRetainerId == 0)
        {
            highlightedRetainerId =
                targetRetainerId;

            originalState =
                OriginalNodeState.Capture(
                    node);
        }

        ApplyConfiguredHighlight(
            node);
    }

    public void Clear()
    {
        if (highlightedRetainerId == 0)
        {
            originalState = null;
            return;
        }

        var addonAddress =
            gameGui.GetAddonByName(
                "RetainerList",
                1);

        if (addonAddress != IntPtr.Zero &&
            originalState.HasValue)
        {
            var node =
                FindHighlightNode(
                    highlightedRetainerId);

            if (node != null)
            {
                originalState.Value.Restore(
                    node);
            }
        }

        highlightedRetainerId = 0;
        originalState = null;
    }

    private ulong GetTargetRetainerId(
        ulong currentCharacterId)
    {
        if (currentCharacterId == 0)
            return 0;

        var action =
            executionRuntime.Snapshot.CurrentAction;

        if (action is null ||
            action.Type != PlannerActionType.Move ||
            action.Source is null ||
            action.Source.Storage != StorageType.Retainer)
        {
            return 0;
        }

        if (action.Source.ParentCharacterId == 0 ||
            action.Source.ParentCharacterId != currentCharacterId)
        {
            return 0;
        }

        return action.Source.OwnerId;
    }

    private AtkResNode* FindHighlightNode(
        ulong retainerId)
    {
        var addonAddress =
            gameGui.GetAddonByName(
                "RetainerList",
                1);

        if (addonAddress == IntPtr.Zero)
            return null;

        var addon =
            (AtkUnitBase*)addonAddress;

        if (addon == null)
            return null;

        var listNode =
            (AtkComponentNode*)addon->GetNodeById(
                ListComponentNodeId);

        if (listNode == null ||
            listNode->Component == null ||
            (ushort)listNode->AtkResNode.Type < 1000)
        {
            return null;
        }

        var retainerManager =
            RetainerManager.Instance();

        if (retainerManager == null)
            return null;

        var count =
            retainerManager->GetRetainerCount();

        for (uint index = 0;
             index < count;
             index++)
        {
            var retainer =
                retainerManager->GetRetainerBySortedIndex(
                    index);

            if (retainer == null ||
                retainer->RetainerId != retainerId)
            {
                continue;
            }

            var rendererNodeId =
                index == 0
                    ? 4U
                    : 41000U + index;

            var renderer =
                FindNodeById(
                    listNode->Component->UldManager,
                    rendererNodeId);

            if (renderer == null ||
                (ushort)renderer->Type < 1000)
            {
                return null;
            }

            var componentNode =
                (AtkComponentNode*)renderer;

            if (componentNode->Component == null)
                return null;

            return componentNode->Component->UldManager.SearchNodeById(
                RowHighlightNodeId);
        }

        return null;
    }

    private void ApplyConfiguredHighlight(
        AtkResNode* node)
    {
        var colour =
            plugin.Configuration.RetainerRowHighlightColor;

        var alpha =
            ToByte(colour.W);

        var addRed =
            ToAdditive(colour.X);

        var addGreen =
            ToAdditive(colour.Y);

        var addBlue =
            ToAdditive(colour.Z);

        var isVisible =
            node->IsVisible();

        if (!isVisible)
        {
            node->NodeFlags |=
                NodeFlags.Visible;
        }

        if (node->Color.A != alpha)
            node->Color.A = alpha;

        if (node->AddRed != addRed)
            node->AddRed = addRed;

        if (node->AddGreen != addGreen)
            node->AddGreen = addGreen;

        if (node->AddBlue != addBlue)
            node->AddBlue = addBlue;
    }

    private void ForgetDestroyedAddon()
    {
        highlightedRetainerId = 0;
        originalState = null;
    }

    private static AtkResNode* FindNodeById(
        AtkUldManager manager,
        uint nodeId)
    {
        for (var index = 0;
             index < manager.NodeListCount;
             index++)
        {
            var node =
                manager.NodeList[index];

            if (node != null &&
                node->NodeId == nodeId)
            {
                return node;
            }
        }

        return null;
    }

    private static byte ToByte(
        float value) =>
        (byte)Math.Clamp(
            (int)Math.Round(value * 255f),
            0,
            255);

    private static short ToAdditive(
        float value) =>
        (short)Math.Clamp(
            (int)Math.Round(value * 255f),
            0,
            255);

    private readonly record struct OriginalNodeState(
        bool WasVisible,
        byte Alpha,
        short AddRed,
        short AddGreen,
        short AddBlue)
    {
        public static OriginalNodeState Capture(
            AtkResNode* node) =>
            new(
                node->IsVisible(),
                node->Color.A,
                node->AddRed,
                node->AddGreen,
                node->AddBlue);

        public void Restore(
            AtkResNode* node)
        {
            node->Color.A = Alpha;
            node->AddRed = AddRed;
            node->AddGreen = AddGreen;
            node->AddBlue = AddBlue;

            if (WasVisible)
            {
                node->NodeFlags |=
                    NodeFlags.Visible;
            }
            else
            {
                node->NodeFlags &=
                    ~NodeFlags.Visible;
            }
        }
    }
}
