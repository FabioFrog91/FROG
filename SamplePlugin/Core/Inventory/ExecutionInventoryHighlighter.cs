using CriticalCommonLib.Addons;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace FROG.Core.Inventory;

/// <summary>
/// Passive execution highlighting for inventory tabs and slots.
/// Resolves UI nodes fresh, changes presentation fields only, and restores the
/// exact original visual state while the same addon instance is alive.
/// </summary>
public sealed unsafe class ExecutionInventoryHighlighter
{
    private const uint SlotNodeOffset = 3;

    private readonly IGameGui gameGui;
    private readonly PlanExecutionRuntime executionRuntime;
    private readonly RetainerDisplayLocator retainerDisplayLocator;
    private readonly CharacterDisplayLocator characterDisplayLocator;
    private readonly Plugin plugin;

    private PlannerPlan? cachedPlan;
    private int cachedActionIndex = -1;
    private HighlightTarget? cachedTarget;

    private readonly List<NodeBinding> activeBindings = new();
    private string activeVisualKey = string.Empty;

    public ExecutionInventoryHighlighter(
        IGameGui gameGui,
        PlanExecutionRuntime executionRuntime,
        RetainerDisplayLocator retainerDisplayLocator,
        CharacterDisplayLocator characterDisplayLocator,
        Plugin plugin)
    {
        this.gameGui = gameGui;
        this.executionRuntime = executionRuntime;
        this.retainerDisplayLocator = retainerDisplayLocator;
        this.characterDisplayLocator = characterDisplayLocator;
        this.plugin = plugin;
    }

    public void Update(
        ulong currentCharacterId)
    {
        var runtime =
            executionRuntime.Snapshot;

        if (!plugin.Configuration.EnableRetainerRowHighlight ||
            runtime.IsReplanning ||
            runtime.Status == PlanExecutionCoordinatorStatus.Complete ||
            runtime.Session is null ||
            runtime.CurrentAction is null)
        {
            Clear();
            InvalidateTargetCache();
            return;
        }

        var actionIndex =
            runtime.Session.CurrentActionIndex - 1;

        if (!ReferenceEquals(
                cachedPlan,
                runtime.Session.Plan) ||
            cachedActionIndex != actionIndex)
        {
            Clear();
            cachedPlan = runtime.Session.Plan;
            cachedActionIndex = actionIndex;
            cachedTarget =
                BuildTarget(
                    runtime.Session.Plan,
                    actionIndex,
                    currentCharacterId);
        }

        var target =
            cachedTarget;

        if (target is null ||
            target.OwnerCharacterId != currentCharacterId)
        {
            Clear();
            return;
        }

        if (target.Storage == StorageType.Retainer)
        {
            UpdateRetainer(
                target);
            return;
        }

        if (target.Storage == StorageType.CharacterInventory)
        {
            UpdateCharacterInventory(
                target);
            return;
        }

        Clear();
    }

    public void Clear()
    {
        foreach (var binding in activeBindings)
        {
            binding.Restore(
                gameGui);
        }

        activeBindings.Clear();
        activeVisualKey = string.Empty;
    }

    private HighlightTarget? BuildTarget(
        PlannerPlan plan,
        int actionIndex,
        ulong currentCharacterId)
    {
        if (actionIndex < 0 ||
            actionIndex >= plan.Actions.Count)
        {
            return null;
        }

        var action =
            plan.Actions[actionIndex];

        if (action.Type != PlannerActionType.Move ||
            action.Source is null ||
            action.Destination is null)
        {
            return null;
        }

        if (action.Source.Storage == StorageType.Retainer)
        {
            if (action.Source.ParentCharacterId == 0 ||
                action.Source.ParentCharacterId != currentCharacterId)
            {
                return null;
            }

            var location =
                retainerDisplayLocator.LocateSource(
                    plan,
                    actionIndex);

            if (!location.IsAvailable)
                return null;

            return new HighlightTarget(
                StorageType.Retainer,
                action.Source.OwnerId,
                action.Source.ParentCharacterId,
                location.Positions
                    .Select(position =>
                        new HighlightPosition(
                            position.Page,
                            position.Slot,
                            position.Quantity))
                    .ToArray());
        }

        if (action.Source.Storage ==
                StorageType.CharacterInventory &&
            action.Source.OwnerId ==
                currentCharacterId &&
            currentCharacterId !=
                plan.InitialState.MainCharacterId &&
            action.Destination.Storage ==
                StorageType.FreeCompanyChest)
        {
            var location =
                characterDisplayLocator.LocateSource(
                    plan,
                    actionIndex);

            if (!location.IsAvailable)
                return null;

            return new HighlightTarget(
                StorageType.CharacterInventory,
                action.Source.OwnerId,
                action.Source.OwnerId,
                location.Positions
                    .Select(position =>
                        new HighlightPosition(
                            position.Page,
                            position.Slot,
                            position.Quantity))
                    .ToArray());
        }

        return null;
    }

    private void UpdateRetainer(
        HighlightTarget target)
    {
        var retainerManager =
            RetainerManager.Instance();

        var activeRetainer =
            retainerManager == null
                ? null
                : retainerManager->GetActiveRetainer();

        if (activeRetainer == null ||
            activeRetainer->RetainerId != target.SourceOwnerId)
        {
            Clear();
            return;
        }

        if (TryGetAddon(
                "InventoryRetainerLarge",
                out var largeAddon))
        {
            var currentTab =
                ((InventoryRetainerLargeAddon*)largeAddon)
                    ->CurrentTab;

            var targetTab =
                GetRetainerLargeTab(
                    target.Positions[0].Page);

            var visualKey =
                $"retainer-large:{target.SourceOwnerId}:{targetTab}:{currentTab}:{BuildPositionKey(target.Positions)}";

            EnsureVisualState(
                visualKey,
                () =>
                {
                    AddBinding(
                        "InventoryRetainerLarge",
                        (uint)(3 + targetTab));

                    foreach (var position in target.Positions)
                    {
                        if (GetRetainerLargeTab(position.Page) !=
                            currentTab)
                        {
                            continue;
                        }

                        AddBinding(
                            $"RetainerGrid{position.Page - 1}",
                            GetSlotNodeId(
                                position.Slot));
                    }
                });

            ApplyConfiguredColour();
            return;
        }

        if (TryGetAddon(
                "InventoryRetainer",
                out var standardAddon))
        {
            var currentTab =
                ((InventoryRetainerAddon*)standardAddon)
                    ->CurrentTab;

            var targetTab =
                target.Positions[0].Page - 1;

            var visualKey =
                $"retainer-standard:{target.SourceOwnerId}:{targetTab}:{currentTab}:{BuildPositionKey(target.Positions)}";

            EnsureVisualState(
                visualKey,
                () =>
                {
                    AddBinding(
                        "InventoryRetainer",
                        (uint)(3 + targetTab));

                    foreach (var position in target.Positions)
                    {
                        if (position.Page - 1 !=
                            currentTab)
                        {
                            continue;
                        }

                        AddBinding(
                            "RetainerGrid",
                            GetSlotNodeId(
                                position.Slot));
                    }
                });

            ApplyConfiguredColour();
            return;
        }

        Clear();
    }

    private void UpdateCharacterInventory(
        HighlightTarget target)
    {
        if (TryGetAddon(
                "InventoryExpansion",
                out _))
        {
            var visualKey =
                $"character-expansion:{target.SourceOwnerId}:{BuildPositionKey(target.Positions)}";

            EnsureVisualState(
                visualKey,
                () =>
                {
                    foreach (var position in target.Positions)
                    {
                        AddBinding(
                            $"InventoryGrid{position.Page - 1}E",
                            GetSlotNodeId(
                                position.Slot));
                    }
                });

            ApplyConfiguredColour();
            return;
        }

        if (TryGetAddon(
                "InventoryLarge",
                out var largeAddon))
        {
            var currentTab =
                ((InventoryLargeAddon*)largeAddon)
                    ->CurrentTab;

            var targetTab =
                GetCharacterLargeTab(
                    target.Positions[0].Page);

            var visualKey =
                $"character-large:{target.SourceOwnerId}:{targetTab}:{currentTab}:{BuildPositionKey(target.Positions)}";

            EnsureVisualState(
                visualKey,
                () =>
                {
                    AddBinding(
                        "InventoryLarge",
                        (uint)(7 + targetTab));

                    foreach (var position in target.Positions)
                    {
                        if (GetCharacterLargeTab(position.Page) !=
                            currentTab)
                        {
                            continue;
                        }

                        var grid =
                            position.Page is 1 or 3
                                ? "InventoryGrid0"
                                : "InventoryGrid1";

                        AddBinding(
                            grid,
                            GetSlotNodeId(
                                position.Slot));
                    }
                });

            ApplyConfiguredColour();
            return;
        }

        if (TryGetAddon(
                "InventoryGrid",
                out var standardAddon))
        {
            var currentTab =
                ((InventoryGridAddon*)standardAddon)
                    ->CurrentTab;

            var targetTab =
                target.Positions[0].Page - 1;

            var visualKey =
                $"character-standard:{target.SourceOwnerId}:{targetTab}:{currentTab}:{BuildPositionKey(target.Positions)}";

            EnsureVisualState(
                visualKey,
                () =>
                {
                    AddBinding(
                        "Inventory",
                        (uint)(8 + targetTab));

                    foreach (var position in target.Positions)
                    {
                        if (position.Page - 1 !=
                            currentTab)
                        {
                            continue;
                        }

                        AddBinding(
                            "InventoryGrid",
                            GetSlotNodeId(
                                position.Slot));
                    }
                });

            ApplyConfiguredColour();
            return;
        }

        Clear();
    }

    private void EnsureVisualState(
        string visualKey,
        Action rebuild)
    {
        if (string.Equals(
                activeVisualKey,
                visualKey,
                StringComparison.Ordinal))
        {
            return;
        }

        Clear();
        activeVisualKey = visualKey;
        rebuild();
    }

    private void AddBinding(
        string addonName,
        uint nodeId)
    {
        if (!TryResolveNode(
                addonName,
                nodeId,
                out var addonAddress,
                out var node))
        {
            return;
        }

        if ((ushort)node->Type < 1000)
            return;

        activeBindings.Add(
            new NodeBinding(
                addonName,
                nodeId,
                addonAddress,
                NodeVisualState.Capture(
                    node)));
    }

    private void ApplyConfiguredColour()
    {
        var colour =
            plugin.Configuration.RetainerRowHighlightColor;

        foreach (var binding in activeBindings)
        {
            if (!binding.TryResolveSameAddon(
                    gameGui,
                    out var node))
            {
                continue;
            }

            ApplyColour(
                node,
                colour);
        }
    }

    private static void ApplyColour(
        AtkResNode* node,
        Vector4 colour)
    {
        var alpha =
            ToByte(colour.W);

        var addRed =
            ToAdditive(colour.X);

        var addGreen =
            ToAdditive(colour.Y);

        var addBlue =
            ToAdditive(colour.Z);

        if (node->Color.A != alpha)
            node->Color.A = alpha;

        if (node->AddRed != addRed)
            node->AddRed = addRed;

        if (node->AddGreen != addGreen)
            node->AddGreen = addGreen;

        if (node->AddBlue != addBlue)
            node->AddBlue = addBlue;
    }

    private bool TryGetAddon(
        string addonName,
        out AtkUnitBase* addon)
    {
        var wrapper =
            gameGui.GetAddonByName(
                addonName,
                1);

        addon =
            wrapper == IntPtr.Zero
                ? null
                : (AtkUnitBase*)wrapper.Address;

        return addon != null &&
               addon->IsVisible;
    }

    private bool TryResolveNode(
        string addonName,
        uint nodeId,
        out nint addonAddress,
        out AtkResNode* node)
    {
        addonAddress = 0;
        node = null;

        if (!TryGetAddon(
                addonName,
                out var addon))
        {
            return false;
        }

        addonAddress =
            (nint)addon;

        node =
            addon->GetNodeById(
                nodeId);

        return node != null;
    }

    private void InvalidateTargetCache()
    {
        cachedPlan = null;
        cachedActionIndex = -1;
        cachedTarget = null;
    }

    private static int GetRetainerLargeTab(
        int page) =>
        page switch
        {
            1 or 2 => 0,
            3 or 4 => 1,
            5 => 2,
            _ => -1
        };

    private static int GetCharacterLargeTab(
        int page) =>
        page switch
        {
            1 or 2 => 0,
            3 or 4 => 1,
            _ => -1
        };

    private static uint GetSlotNodeId(
        int slot) =>
        checked(
            (uint)(slot - 1) +
            SlotNodeOffset);

    private static string BuildPositionKey(
        IReadOnlyList<HighlightPosition> positions) =>
        string.Join(
            ",",
            positions.Select(position =>
                $"{position.Page}:{position.Slot}:{position.Quantity}"));

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

    private sealed record HighlightTarget(
        StorageType Storage,
        ulong SourceOwnerId,
        ulong OwnerCharacterId,
        IReadOnlyList<HighlightPosition> Positions);

    private sealed record HighlightPosition(
        int Page,
        int Slot,
        int Quantity);

    private readonly record struct NodeVisualState(
        byte Alpha,
        short AddRed,
        short AddGreen,
        short AddBlue)
    {
        public static NodeVisualState Capture(
            AtkResNode* node) =>
            new(
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
        }
    }

    private readonly record struct NodeBinding(
        string AddonName,
        uint NodeId,
        nint AddonAddress,
        NodeVisualState Original)
    {
        public bool TryResolveSameAddon(
            IGameGui gameGui,
            out AtkResNode* node)
        {
            node = null;

            var wrapper =
                gameGui.GetAddonByName(
                    AddonName,
                    1);

            if (wrapper == IntPtr.Zero ||
                wrapper.Address != AddonAddress)
            {
                return false;
            }

            var addon =
                (AtkUnitBase*)wrapper.Address;

            if (addon == null ||
                !addon->IsVisible)
            {
                return false;
            }

            node =
                addon->GetNodeById(
                    NodeId);

            return node != null;
        }

        public void Restore(
            IGameGui gameGui)
        {
            if (!TryResolveSameAddon(
                    gameGui,
                    out var node))
            {
                return;
            }

            Original.Restore(
                node);
        }
    }
}
