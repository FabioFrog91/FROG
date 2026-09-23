using CriticalCommonLib.Addons;
using CriticalCommonLib.Models;
using CriticalCommonLib.Services;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace FROG.Core.Inventory;

public readonly record struct ExecutionQuantityBadgeAnchor(
    string AddonName,
    uint NodeId,
    nint AddonAddress,
    int Quantity);

/// <summary>
/// Passive execution highlighting for inventory tabs and slots.
/// Resolves UI nodes fresh, changes presentation fields only, and restores the
/// exact original visual state while the same addon instance is alive.
/// </summary>
public sealed unsafe class ExecutionInventoryHighlighter : IDisposable
{
    private const uint SlotNodeOffset = 3;
    private const uint FreeCompanySlotNodeOffset = 23;
    private const uint FreeCompanyTabNodeOffset = 10;
    private const int TargetRetryIntervalMs = 100;

    private readonly IGameGui gameGui;
    private readonly ICharacterMonitor characterMonitor;
    private readonly IOdrScanner odrScanner;
    private readonly PlanExecutionRuntime executionRuntime;
    private readonly RetainerDisplayLocator retainerDisplayLocator;
    private readonly CharacterDisplayLocator characterDisplayLocator;
    private readonly FreeCompanyDisplayLocator freeCompanyDisplayLocator;
    private readonly Plugin plugin;

    private PlannerPlan? cachedPlan;
    private int cachedActionIndex = -1;
    private ulong cachedCharacterId;
    private string cachedInstructionKey = string.Empty;
    private HighlightTarget? cachedTarget;
    private bool targetRefreshRequested;
    private bool retryTargetWhileUnavailable;
    private long nextTargetRetryAtMs;

    private readonly List<NodeBinding> activeBindings = new();
    private readonly List<ExecutionQuantityBadgeAnchor> quantityBadgeAnchors = new();
    private string activeVisualKey = string.Empty;

    public IReadOnlyList<ExecutionQuantityBadgeAnchor> QuantityBadgeAnchors =>
        quantityBadgeAnchors;

    public ExecutionInventoryHighlighter(
        IGameGui gameGui,
        ICharacterMonitor characterMonitor,
        IOdrScanner odrScanner,
        PlanExecutionRuntime executionRuntime,
        RetainerDisplayLocator retainerDisplayLocator,
        CharacterDisplayLocator characterDisplayLocator,
        FreeCompanyDisplayLocator freeCompanyDisplayLocator,
        Plugin plugin)
    {
        this.gameGui = gameGui;
        this.characterMonitor = characterMonitor;
        this.odrScanner = odrScanner;
        this.executionRuntime = executionRuntime;
        this.retainerDisplayLocator = retainerDisplayLocator;
        this.characterDisplayLocator = characterDisplayLocator;
        this.freeCompanyDisplayLocator = freeCompanyDisplayLocator;
        this.plugin = plugin;

        odrScanner.OnSortOrderChanged +=
            OnSortOrderChanged;
    }

    public void Update(
        ulong currentCharacterId)
    {
        var runtime =
            executionRuntime.Snapshot;

        if (!plugin.Configuration.EnableInventoryExecutionHighlight ||
            runtime.IsReplanning ||
            runtime.Status ==
                PlanExecutionCoordinatorStatus.WaitingForCapacity ||
            runtime.Status ==
                PlanExecutionCoordinatorStatus.Complete ||
            runtime.Session is null ||
            runtime.CurrentInstruction is null)
        {
            Clear();
            InvalidateTargetCache();
            return;
        }

        var instruction =
            runtime.CurrentInstruction;

        var actionIndex =
            runtime.Session.CurrentDecisionIndex -
            1;

        var nowMs =
            Environment.TickCount64;

        var instructionKey =
            BuildInstructionKey(
                instruction);

        var targetContextChanged =
            !ReferenceEquals(
                cachedPlan,
                runtime.Session.Plan) ||
            cachedActionIndex !=
                actionIndex ||
            cachedCharacterId !=
                currentCharacterId ||
            !string.Equals(
                cachedInstructionKey,
                instructionKey,
                StringComparison.Ordinal);

        var targetNeedsRefresh =
            targetContextChanged ||
            targetRefreshRequested;

        if (targetNeedsRefresh)
        {
            var refreshedTarget =
                BuildTarget(
                    runtime.Session.Plan,
                    instruction,
                    currentCharacterId);

            var targetChanged =
                targetContextChanged ||
                !IsSameTarget(
                    cachedTarget,
                    refreshedTarget);

            if (targetChanged)
                Clear();

            cachedPlan =
                runtime.Session.Plan;
            cachedActionIndex =
                actionIndex;
            cachedCharacterId =
                currentCharacterId;
            cachedInstructionKey =
                instructionKey;
            cachedTarget =
                refreshedTarget;
            targetRefreshRequested =
                false;

            retryTargetWhileUnavailable =
                cachedTarget is null &&
                IsPotentialHighlightTarget(
                    runtime.Session.Plan,
                    instruction,
                    currentCharacterId);

            nextTargetRetryAtMs =
                nowMs +
                TargetRetryIntervalMs;
        }
        else if (cachedTarget is null &&
                 retryTargetWhileUnavailable &&
                 nowMs >= nextTargetRetryAtMs)
        {
            nextTargetRetryAtMs =
                nowMs +
                TargetRetryIntervalMs;

            if (IsRelevantSourceUiVisible(
                    instruction))
            {
                cachedTarget =
                    BuildTarget(
                        runtime.Session.Plan,
                        instruction,
                        currentCharacterId);

                if (cachedTarget is not null)
                {
                    retryTargetWhileUnavailable =
                        false;
                }
            }
        }

        var target =
            cachedTarget;

        if (target is null ||
            target.OwnerCharacterId !=
                currentCharacterId)
        {
            Clear();
            return;
        }

        if (target.Storage ==
            StorageType.Retainer)
        {
            UpdateRetainer(
                target);
            return;
        }

        if (target.Storage ==
            StorageType.CharacterInventory)
        {
            UpdateCharacterInventory(
                target);
            return;
        }

        if (target.Storage ==
            StorageType.FreeCompanyChest)
        {
            UpdateFreeCompanyChest(
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
        quantityBadgeAnchors.Clear();
        activeVisualKey = string.Empty;
    }

    public void Dispose()
    {
        odrScanner.OnSortOrderChanged -=
            OnSortOrderChanged;

        Clear();
        InvalidateTargetCache();
    }

    private void OnSortOrderChanged(
        InventorySortOrder sortOrder)
    {
        targetRefreshRequested = true;
    }

    private HighlightTarget? BuildTarget(
        PlannerPlan plan,
        ExecutionInstruction instruction,
        ulong currentCharacterId)
    {
        var decision =
            instruction.Decision;

        if (decision.Type !=
                PlannerDecisionType.Move ||
            decision.Source is null ||
            decision.Destination is null)
        {
            return null;
        }

        if (decision.Source.Storage ==
            StorageType.Retainer)
        {
            if (decision.Source.ParentCharacterId == 0 ||
                decision.Source.ParentCharacterId !=
                    currentCharacterId)
            {
                return null;
            }

            var location =
                retainerDisplayLocator.LocateSource(
                    instruction);

            if (!location.IsAvailable)
                return null;

            return new HighlightTarget(
                StorageType.Retainer,
                decision.Source.OwnerId,
                decision.Source.ParentCharacterId,
                location.Positions
                    .Select(position =>
                        new HighlightPosition(
                            position.Page,
                            position.Slot,
                            position.Quantity))
                    .ToArray());
        }

        if (decision.Source.Storage ==
                StorageType.CharacterInventory &&
            decision.Source.OwnerId ==
                currentCharacterId &&
            currentCharacterId !=
                plan.InitialState.MainCharacterId &&
            decision.Destination.Storage ==
                StorageType.FreeCompanyChest)
        {
            var location =
                characterDisplayLocator.LocateSource(
                    instruction);

            if (!location.IsAvailable)
                return null;

            return new HighlightTarget(
                StorageType.CharacterInventory,
                decision.Source.OwnerId,
                decision.Source.OwnerId,
                location.Positions
                    .Select(position =>
                        new HighlightPosition(
                            position.Page,
                            position.Slot,
                            position.Quantity))
                    .ToArray());
        }

        if (decision.Source.Storage ==
                StorageType.FreeCompanyChest &&
            decision.Destination.Storage ==
                StorageType.CharacterInventory &&
            decision.Destination.OwnerId ==
                currentCharacterId)
        {
            var location =
                freeCompanyDisplayLocator.LocateSource(
                    instruction);

            if (!location.IsAvailable)
                return null;

            return new HighlightTarget(
                StorageType.FreeCompanyChest,
                decision.Source.OwnerId,
                decision.Destination.OwnerId,
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

    private static bool IsSameTarget(
        HighlightTarget? left,
        HighlightTarget? right)
    {
        if (ReferenceEquals(
                left,
                right))
        {
            return true;
        }

        if (left is null ||
            right is null ||
            left.Storage != right.Storage ||
            left.SourceOwnerId != right.SourceOwnerId ||
            left.OwnerCharacterId != right.OwnerCharacterId ||
            left.Positions.Count != right.Positions.Count)
        {
            return false;
        }

        for (var index = 0;
             index < left.Positions.Count;
             index++)
        {
            if (left.Positions[index] !=
                right.Positions[index])
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPotentialHighlightTarget(
        PlannerPlan plan,
        ExecutionInstruction instruction,
        ulong currentCharacterId)
    {
        if (currentCharacterId == 0)
            return false;

        var decision =
            instruction.Decision;

        if (decision.Type !=
                PlannerDecisionType.Move ||
            decision.Source is null ||
            decision.Destination is null)
        {
            return false;
        }

        if (decision.Source.Storage ==
            StorageType.Retainer)
        {
            return decision.Source.ParentCharacterId ==
                currentCharacterId;
        }

        if (decision.Source.Storage ==
            StorageType.FreeCompanyChest)
        {
            return decision.Destination.Storage ==
                       StorageType.CharacterInventory &&
                   decision.Destination.OwnerId ==
                       currentCharacterId;
        }

        return decision.Source.Storage ==
                   StorageType.CharacterInventory &&
               decision.Source.OwnerId ==
                   currentCharacterId &&
               currentCharacterId !=
                   plan.InitialState.MainCharacterId &&
               decision.Destination.Storage ==
                   StorageType.FreeCompanyChest;
    }

    private bool IsRelevantSourceUiVisible(
        ExecutionInstruction instruction)
    {
        var source =
            instruction.Decision.Source;

        if (source?.Storage ==
            StorageType.Retainer)
        {
            return IsAddonVisible(
                       "RetainerList") ||
                   IsAddonVisible(
                       "InventoryRetainer") ||
                   IsAddonVisible(
                       "InventoryRetainerLarge");
        }

        if (source?.Storage ==
            StorageType.CharacterInventory)
        {
            return IsAddonVisible(
                       "InventoryGrid") ||
                   IsAddonVisible(
                       "InventoryLarge") ||
                   IsAddonVisible(
                       "InventoryExpansion");
        }

        if (source?.Storage ==
            StorageType.FreeCompanyChest)
        {
            return IsAddonVisible(
                "FreeCompanyChest");
        }

        return false;
    }

    private bool IsAddonVisible(
        string addonName) =>
        TryGetAddon(
            addonName,
            out _);

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

            var expectedBindings =
                1 +
                target.Positions.Count(position =>
                    GetRetainerLargeTab(position.Page) ==
                    currentTab);

            EnsureVisualState(
                visualKey,
                expectedBindings,
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

                        AddSlotBinding(
                            $"RetainerGrid{position.Page - 1}",
                            GetSlotNodeId(
                                position.Slot),
                            position.Quantity);
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

            var expectedBindings =
                1 +
                target.Positions.Count(position =>
                    position.Page - 1 ==
                    currentTab);

            EnsureVisualState(
                visualKey,
                expectedBindings,
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

                        AddSlotBinding(
                            "RetainerGrid",
                            GetSlotNodeId(
                                position.Slot),
                            position.Quantity);
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
                target.Positions.Count,
                () =>
                {
                    foreach (var position in target.Positions)
                    {
                        AddSlotBinding(
                            $"InventoryGrid{position.Page - 1}E",
                            GetSlotNodeId(
                                position.Slot),
                            position.Quantity);
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

            var expectedBindings =
                1 +
                target.Positions.Count(position =>
                    GetCharacterLargeTab(position.Page) ==
                    currentTab);

            EnsureVisualState(
                visualKey,
                expectedBindings,
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

                        AddSlotBinding(
                            grid,
                            GetSlotNodeId(
                                position.Slot),
                            position.Quantity);
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

            var expectedBindings =
                1 +
                target.Positions.Count(position =>
                    position.Page - 1 ==
                    currentTab);

            EnsureVisualState(
                visualKey,
                expectedBindings,
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

                        AddSlotBinding(
                            "InventoryGrid",
                            GetSlotNodeId(
                                position.Slot),
                            position.Quantity);
                    }
                });

            ApplyConfiguredColour();
            return;
        }

        Clear();
    }

    private void UpdateFreeCompanyChest(
        HighlightTarget target)
    {
        if (characterMonitor.ActiveFreeCompanyId == 0 ||
            characterMonitor.ActiveFreeCompanyId != target.SourceOwnerId ||
            !TryGetAddon(
                "FreeCompanyChest",
                out var addon))
        {
            Clear();
            return;
        }

        var currentTab =
            GetFreeCompanyTabIndex(
                ((InventoryFreeCompanyChestAddon*)addon)
                    ->CurrentTab);

        var targetTab =
            target.Positions[0].Page - 1;

        var visualKey =
            $"free-company:{target.SourceOwnerId}:{targetTab}:{currentTab}:{BuildPositionKey(target.Positions)}";

        var expectedBindings =
            1 +
            target.Positions.Count(position =>
                position.Page - 1 == currentTab);

        EnsureVisualState(
            visualKey,
            expectedBindings,
            () =>
            {
                AddBinding(
                    "FreeCompanyChest",
                    FreeCompanyTabNodeOffset +
                    checked((uint)targetTab));

                foreach (var position in target.Positions)
                {
                    if (position.Page - 1 != currentTab)
                        continue;

                    AddSlotBinding(
                        "FreeCompanyChest",
                        FreeCompanySlotNodeOffset +
                        checked((uint)(position.Slot - 1)),
                        position.Quantity);
                }
            });

        ApplyConfiguredColour();
    }

    private void EnsureVisualState(
        string visualKey,
        int expectedBindings,
        Action rebuild)
    {
        var sameVisualState =
            string.Equals(
                activeVisualKey,
                visualKey,
                StringComparison.Ordinal);

        if (sameVisualState &&
            activeBindings.Count == expectedBindings &&
            activeBindings.All(binding =>
                binding.IsAlive(
                    gameGui)))
        {
            return;
        }

        Clear();
        activeVisualKey = visualKey;
        rebuild();
    }

    private bool AddBinding(
        string addonName,
        uint nodeId)
    {
        if (!TryResolveNode(
                addonName,
                nodeId,
                out var addonAddress,
                out var node))
        {
            return false;
        }

        if ((ushort)node->Type < 1000)
            return false;

        activeBindings.Add(
            new NodeBinding(
                addonName,
                nodeId,
                addonAddress,
                NodeVisualState.Capture(
                    node)));

        return true;
    }

    private void AddSlotBinding(
        string addonName,
        uint nodeId,
        int quantity)
    {
        if (!AddBinding(
                addonName,
                nodeId))
        {
            return;
        }

        quantityBadgeAnchors.Add(
            new ExecutionQuantityBadgeAnchor(
                addonName,
                nodeId,
                activeBindings[^1].AddonAddress,
                quantity));
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

    private static string BuildInstructionKey(
        ExecutionInstruction instruction) =>
        string.Join(
            "|",
            instruction.SourceStacks.Select(allocation =>
                $"{allocation.Item.Storage}:{allocation.Item.OwnerId}:{allocation.Item.Container}:{allocation.Item.Slot}:{allocation.Quantity}"));

    private void InvalidateTargetCache()
    {
        cachedPlan = null;
        cachedActionIndex = -1;
        cachedCharacterId = 0;
        cachedInstructionKey = string.Empty;
        cachedTarget = null;
        targetRefreshRequested = false;
        retryTargetWhileUnavailable = false;
        nextTargetRetryAtMs = 0;
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

    private static int GetFreeCompanyTabIndex(
        FreeCompanyTab tab) =>
        tab switch
        {
            FreeCompanyTab.One => 0,
            FreeCompanyTab.Two => 1,
            FreeCompanyTab.Three => 2,
            FreeCompanyTab.Four => 3,
            FreeCompanyTab.Five => 4,
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

        public bool IsAlive(
            IGameGui gameGui) =>
            TryResolveSameAddon(
                gameGui,
                out _);

        public void Restore(
            IGameGui gameGui)
        {
            // A hidden addon can still retain the highlight. Restore its
            // original colour as long as this is the same addon instance.
            var wrapper =
                gameGui.GetAddonByName(
                    AddonName,
                    1);

            if (wrapper == IntPtr.Zero ||
                wrapper.Address != AddonAddress)
            {
                return;
            }

            var addon =
                (AtkUnitBase*)wrapper.Address;

            if (addon == null)
                return;

            var node =
                addon->GetNodeById(
                    NodeId);

            if (node != null)
            {
                Original.Restore(
                    node);
            }
        }
    }
}
