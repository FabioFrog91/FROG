using AllaganLib.Monitors.Services;
using Autofac;
using CriticalCommonLib.Models;
using CriticalCommonLib.Services;
using DalaMock.Host.Hosting;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Command;
using Dalamud.Game.Inventory;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FROG.Core.Inventory;
using FROG.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FROG;

public sealed class Plugin : HostedPlugin
{
    [PluginService]
    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService]
    internal static ITextureProvider TextureProvider { get; private set; } = null!;

    [PluginService]
    internal static ICommandManager CommandManager { get; private set; } = null!;

    [PluginService]
    internal static IClientState ClientState { get; private set; } = null!;

    [PluginService]
    internal static IPlayerState PlayerState { get; private set; } = null!;

    [PluginService]
    internal static IDataManager DataManager { get; private set; } = null!;

    [PluginService]
    internal static IGameInventory GameInventory { get; private set; } = null!;

    [PluginService]
    internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;

    [PluginService]
    internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/frog";
    private const string InventoryIndexFileName = "inventory-index.json";

    private const int LoginRetryIntervalMilliseconds = 500;
    private const int LoginRetryMaxAttempts = 10;

    private CancellationTokenSource? loginSyncCancellation;

    public Configuration Configuration { get; private set; } = null!;

    public InventoryIndex InventoryIndex { get; } = new();

    public WindowSystem WindowSystem { get; } = new("FROG");

    private ConfigWindow ConfigWindow { get; }
    private MainWindow MainWindow { get; }

    private string InventoryIndexFilePath =>
        Path.Combine(
            PluginInterface.ConfigDirectory.FullName,
            InventoryIndexFileName);

    internal IReadOnlyList<InventoryItemSnapshot> LastSyncSnapshots { get; private set; }
        = Array.Empty<InventoryItemSnapshot>();

    internal DateTime? LastSyncAtUtc { get; private set; }

    internal ulong LastSyncCharacterId { get; private set; }

    internal IReadOnlyList<InventoryItemSnapshot> LastSyncIndexSnapshots { get; private set; }
        = Array.Empty<InventoryItemSnapshot>();

    public Plugin(IDalamudPluginInterface pluginInterface)
        : base(pluginInterface)
    {
        Configuration =
            pluginInterface.GetPluginConfig() as Configuration
            ?? new Configuration();

        try
        {
            InventoryIndex.LoadFromDisk(InventoryIndexFilePath);

            Log.Information(
                $"Indice inventario caricato da disco: {InventoryIndexFilePath}");

            Log.Information(
                $"Snapshot caricati: {InventoryIndex.Items.Count}");
        }
        catch (Exception ex)
        {
            Log.Error(
                ex,
                $"Errore durante il caricamento dell'indice inventario: {InventoryIndexFilePath}");
        }

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(
            CommandName,
            new CommandInfo(OnCommand)
            {
                HelpMessage = "Apre l'interfaccia principale di FROG"
            });

        pluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        pluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        pluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        ClientState.Logout += OnLogout;
        ClientState.Login += OnLogin;

        AddonLifecycle.RegisterListener(
            AddonEvent.PostSetup,
            "RetainerList",
            OnRetainerListOpened);

        AddonLifecycle.RegisterListener(
            AddonEvent.PreFinalize,
            "RetainerList",
            OnRetainerListClosed);

        AddonLifecycle.RegisterListener(
            AddonEvent.PostSetup,
            "FreeCompanyChest",
            OnFreeCompanyChestOpened);

        AddonLifecycle.RegisterListener(
            AddonEvent.PreFinalize,
            "FreeCompanyChest",
            OnFreeCompanyChestClosed);

        Log.Information(
            $"=== {pluginInterface.Manifest.Name} avviato ===");
    }

    public override HostedPluginOptions ConfigureOptions()
    {
        return new HostedPluginOptions
        {
            UseMediatorService = false
        };
    }

    public override void ConfigureContainer(ContainerBuilder containerBuilder)
    {
        containerBuilder
            .RegisterInstance(this)
            .AsSelf()
            .SingleInstance();

        CclInventoryBootstrap.Register(containerBuilder);

        RegisterHostedService(typeof(AchievementMonitorService));
        RegisterHostedService(typeof(OdrScanner));
        RegisterHostedService(typeof(FrogInventoryStartup));
    }

    public override void ConfigureServices(
        IServiceCollection serviceCollection)
    {
    }

    public override void Dispose()
    {
        loginSyncCancellation?.Cancel();
        loginSyncCancellation?.Dispose();
        loginSyncCancellation = null;

        ClientState.Login -= OnLogin;
        ClientState.Logout -= OnLogout;

        AddonLifecycle.UnregisterListener(
            AddonEvent.PostSetup,
            "RetainerList",
            OnRetainerListOpened);

        AddonLifecycle.UnregisterListener(
            AddonEvent.PreFinalize,
            "RetainerList",
            OnRetainerListClosed);

        AddonLifecycle.UnregisterListener(
            AddonEvent.PostSetup,
            "FreeCompanyChest",
            OnFreeCompanyChestOpened);

        AddonLifecycle.UnregisterListener(
            AddonEvent.PreFinalize,
            "FreeCompanyChest",
            OnFreeCompanyChestClosed);

        try
        {
            SaveInventoryIndex();
        }
        catch (Exception ex)
        {
            Log.Error(
                ex,
                $"Errore durante il salvataggio dell'indice inventario: {InventoryIndexFilePath}");
        }

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);

        base.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        MainWindow.Toggle();
    }

    public void ToggleConfigUi()
    {
        ConfigWindow.Toggle();
    }

    public void ToggleMainUi()
    {
        MainWindow.Toggle();
    }

    private void OnLogin()
    {
        StartLoginSyncRetry();
    }

    private void OnLogout(int type, int code)
    {
        loginSyncCancellation?.Cancel();

        SaveInventoryIndex();

        LastSyncCharacterId = 0;
        LastSyncAtUtc = null;
        LastSyncSnapshots = Array.Empty<InventoryItemSnapshot>();
        LastSyncIndexSnapshots = Array.Empty<InventoryItemSnapshot>();
    }

    private void StartLoginSyncRetry()
    {
        loginSyncCancellation?.Cancel();
        loginSyncCancellation?.Dispose();

        loginSyncCancellation = new CancellationTokenSource();

        var cancellationToken = loginSyncCancellation.Token;

        _ = RunLoginSyncRetryAsync(cancellationToken);
    }

    private async Task RunLoginSyncRetryAsync(
        CancellationToken cancellationToken)
    {
        for (var attempt = 1;
             attempt <= LoginRetryMaxAttempts;
             attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            await Task.Delay(
                LoginRetryIntervalMilliseconds,
                cancellationToken);

            if (cancellationToken.IsCancellationRequested)
                return;

            if (!PlayerState.IsLoaded)
                continue;

            var characterId = PlayerState.ContentId;

            if (characterId == 0)
                continue;

            // We have successfully obtained the current character.
            // SyncPlayerInventory() performs the complete inventory read.
            SyncPlayerInventory();

            // Verify that the sync actually belongs to this character.
            if (LastSyncCharacterId != characterId)
                continue;

            // The new character has been successfully synchronized.
            // Persist it once and stop retrying immediately.
            SaveInventoryIndex();

            Log.Information(
                $"Sincronizzazione login completata per il personaggio {characterId} al tentativo {attempt}.");

            return;
        }

        Log.Warning(
            $"Sincronizzazione inventario dopo login non completata entro {LoginRetryMaxAttempts * LoginRetryIntervalMilliseconds} ms.");
    }

    internal void SyncPlayerInventory()
    {
        if (!PlayerState.IsLoaded)
            return;

        var characterId = PlayerState.ContentId;

        if (characterId == 0)
            return;

        var observedAtUtc = DateTime.UtcNow;

        // If the logged-in character changed, persist the previous
        // character's current RAM state before synchronizing the new one.
        if (LastSyncCharacterId != 0 &&
            LastSyncCharacterId != characterId)
        {
            SaveInventoryIndex();
        }

        LastSyncCharacterId = characterId;
        LastSyncAtUtc = observedAtUtc;

        var allSnapshots = new List<InventoryItemSnapshot>();

        AddInventorySnapshots(
            allSnapshots,
            GameInventoryType.Inventory1,
            characterId,
            observedAtUtc);

        AddInventorySnapshots(
            allSnapshots,
            GameInventoryType.Inventory2,
            characterId,
            observedAtUtc);

        AddInventorySnapshots(
            allSnapshots,
            GameInventoryType.Inventory3,
            characterId,
            observedAtUtc);

        AddInventorySnapshots(
            allSnapshots,
            GameInventoryType.Inventory4,
            characterId,
            observedAtUtc);

        // GameInventory is the source of truth for the currently
        // logged-in character. Replace the complete character segment
        // of the RAM index with what was just read.
        //
        // No other character or storage source is touched.
        InventoryIndex.ReplaceCharacterInventory(
            characterId,
            allSnapshots);

        LastSyncSnapshots = allSnapshots;

        LastSyncIndexSnapshots = InventoryIndex.Items
            .Where(x =>
                x.Storage == StorageType.CharacterInventory &&
                x.OwnerId == characterId)
            .OrderBy(x => x.Container)
            .ThenBy(x => x.Slot)
            .ToList();

        // No disk write here.
        // RAM is updated immediately. Persistence happens only at
        // explicit checkpoints.
    }

    private static void AddInventorySnapshots(
        List<InventoryItemSnapshot> snapshots,
        GameInventoryType inventoryType,
        ulong characterId,
        DateTime observedAtUtc)
    {
        var inventoryItems =
            GameInventory.GetInventoryItems(inventoryType);

        for (var slot = 0; slot < inventoryItems.Length; slot++)
        {
            var item = inventoryItems[slot];

            if (item.ItemId == 0)
                continue;

            snapshots.Add(
                new InventoryItemSnapshot(
                    item.BaseItemId,
                    item.ItemId,
                    item.Quantity,
                    item.IsHq,
                    StorageType.CharacterInventory,
                    characterId,
                    (uint)inventoryType,
                    slot,
                    observedAtUtc,
                    true));
        }
    }

    private void OnRetainerListOpened(
        AddonEvent type,
        AddonArgs args)
    {
        SaveInventoryIndex();
    }

    private void OnRetainerListClosed(
        AddonEvent type,
        AddonArgs args)
    {
        SaveInventoryIndex();
    }

    private void OnFreeCompanyChestOpened(
        AddonEvent type,
        AddonArgs args)
    {
        SaveInventoryIndex();
    }

    private void OnFreeCompanyChestClosed(
        AddonEvent type,
        AddonArgs args)
    {
        SaveInventoryIndex();
    }

    internal void SaveInventoryIndex()
    {
        if (!InventoryIndex.IsDirty)
            return;

        InventoryIndex.SaveToDisk(InventoryIndexFilePath);

        Log.Information(
            $"Indice inventario salvato: {InventoryIndexFilePath} ({InventoryIndex.Items.Count} snapshot)");
    }
}

internal sealed class FrogInventoryStartup : IHostedService
{
    private readonly IInventoryMonitor inventoryMonitor;
    private readonly IInventoryScanner inventoryScanner;
    private readonly Plugin plugin;

    public FrogInventoryStartup(
        IInventoryMonitor inventoryMonitor,
        IInventoryScanner inventoryScanner,
        Plugin plugin)
    {
        this.inventoryMonitor = inventoryMonitor;
        this.inventoryScanner = inventoryScanner;
        this.plugin = plugin;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        inventoryMonitor.OnInventoryChanged += OnInventoryChanged;

        inventoryMonitor.Start();

        inventoryScanner.Enable();

        inventoryMonitor.SignalRefresh();

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        inventoryMonitor.OnInventoryChanged -= OnInventoryChanged;

        return Task.CompletedTask;
    }

    private void OnInventoryChanged(
        List<InventoryChange> inventoryChanges,
        InventoryMonitor.ItemChanges? itemChanges)
    {
        plugin.SyncPlayerInventory();
    }
}
