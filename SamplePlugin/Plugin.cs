using AllaganLib.Monitors.Services;
using Autofac;
using CriticalCommonLib.Models;
using CriticalCommonLib.Services;
using DalaMock.Host.Hosting;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FROG.Core.Inventory;
using FROG.Core.Inventory.Providers;
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
    private const string CharacterCatalogFileName = "character-catalog.json";

    private const int LoginRetryIntervalMilliseconds = 500;
    private const int LoginRetryMaxAttempts = 10;

    private CancellationTokenSource? loginSyncCancellation;

    private readonly PlayerInventoryAPI playerInventory;

    private CharacterCatalogSync? characterCatalogSync;

    public Configuration Configuration { get; private set; } = null!;

    public InventoryIndex InventoryIndex { get; } = new();

    public CharacterCatalog CharacterCatalog { get; } = new();

    public WindowSystem WindowSystem { get; } = new("FROG");

    private ConfigWindow ConfigWindow { get; }
    private MainWindow? MainWindow { get; set; }

    private string InventoryIndexFilePath =>
        Path.Combine(
            PluginInterface.ConfigDirectory.FullName,
            InventoryIndexFileName);

    private string CharacterCatalogFilePath =>
        Path.Combine(
            PluginInterface.ConfigDirectory.FullName,
            CharacterCatalogFileName);

    internal IReadOnlyList<InventoryItemSnapshot> LastSyncSnapshots { get; private set; }
        = Array.Empty<InventoryItemSnapshot>();

    internal DateTime? LastSyncAtUtc { get; private set; }

    internal ulong LastSyncCharacterId { get; private set; }

    internal IReadOnlyList<InventoryItemSnapshot> LastSyncIndexSnapshots { get; private set; }
        = Array.Empty<InventoryItemSnapshot>();

    public Plugin(IDalamudPluginInterface pluginInterface)
        : base(pluginInterface)
    {
        playerInventory =
            new PlayerInventory(
                GameInventory,
                PlayerState);

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

        try
        {
            CharacterCatalog.LoadFromDisk(
                CharacterCatalogFilePath);

            Log.Information(
                $"Catalogo personaggi caricato da disco: {CharacterCatalogFilePath}");

            Log.Information(
                $"Identità caricate: {CharacterCatalog.Entries.Count}");
        }
        catch (Exception ex)
        {
            Log.Error(
                ex,
                $"Errore durante il caricamento del catalogo personaggi: {CharacterCatalogFilePath}");
        }

        ConfigWindow = new ConfigWindow(this);

        WindowSystem.AddWindow(ConfigWindow);

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

        containerBuilder
            .RegisterType<StorageReader>()
            .As<StorageReaderAPI>()
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

    public override Task StartedAsync()
    {
        if (Host == null)
        {
            Log.Error(
                "Impossibile creare MainWindow: Host non disponibile.");

            return Task.CompletedTask;
        }

        var characterMonitor =
            Host.Services.GetRequiredService<ICharacterMonitor>();

        characterCatalogSync =
            new CharacterCatalogSync(
                characterMonitor,
                CharacterCatalog);

        characterCatalogSync.SyncAll();

        characterMonitor.OnCharacterUpdated +=
            OnCharacterUpdated;

        MainWindow =
            new MainWindow(
                this,
                characterMonitor,
                CharacterCatalog);

        WindowSystem.AddWindow(MainWindow);

        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        loginSyncCancellation?.Cancel();
        loginSyncCancellation?.Dispose();
        loginSyncCancellation = null;

        ClientState.Login -= OnLogin;
        ClientState.Logout -= OnLogout;

        if (Host != null)
        {
            var characterMonitor =
                Host.Services.GetService<ICharacterMonitor>();

            if (characterMonitor != null)
            {
                characterMonitor.OnCharacterUpdated -=
                    OnCharacterUpdated;
            }
        }

        characterCatalogSync?.SyncAll();

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
                $"Errore durante il salvataggio dello stato persistente.");
        }

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow?.Dispose();

        CommandManager.RemoveHandler(CommandName);

        base.Dispose();
    }

    private void OnCommand(
        string command,
        string args)
    {
        ToggleMainUi();
    }

    public void ToggleConfigUi()
    {
        ConfigWindow.Toggle();
    }

    public void ToggleMainUi()
    {
        MainWindow?.Toggle();
    }

    private void OnLogin()
    {
        StartLoginSyncRetry();
    }

    private void OnLogout(
        int type,
        int code)
    {
        loginSyncCancellation?.Cancel();

        characterCatalogSync?.SyncAll();

        SaveInventoryIndex();

        LastSyncCharacterId = 0;
        LastSyncAtUtc = null;
        LastSyncSnapshots = Array.Empty<InventoryItemSnapshot>();
        LastSyncIndexSnapshots = Array.Empty<InventoryItemSnapshot>();
    }

    private void OnCharacterUpdated(
        Character? character)
    {
        if (character is null)
            return;

        characterCatalogSync?.Sync(character);
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

            if (!playerInventory.TryGetCurrentCharacter(
                    out var characterId))
            {
                continue;
            }

            SyncPlayerInventory();

            if (LastSyncCharacterId != characterId)
                continue;

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
        if (!playerInventory.TryGetCurrentCharacter(
                out var characterId))
        {
            return;
        }

        var observedAtUtc = DateTime.UtcNow;

        if (LastSyncCharacterId != 0 &&
            LastSyncCharacterId != characterId)
        {
            SaveInventoryIndex();
        }

        LastSyncCharacterId = characterId;
        LastSyncAtUtc = observedAtUtc;

        var allSnapshots =
            playerInventory.ReadCurrentInventory(
                characterId,
                observedAtUtc);

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
    }

    internal void SyncStorageSources(
        StorageReaderAPI storageReader)
    {
        var observedAtUtc = DateTime.UtcNow;

        if (storageReader.TryReadActiveRetainer(
                observedAtUtc,
                out var retainerSources,
                out var retainerSnapshots))
        {
            foreach (var source in retainerSources)
            {
                var sourceSnapshots = retainerSnapshots
                    .Where(x =>
                        x.Storage == source.Storage &&
                        x.OwnerId == source.OwnerId &&
                        x.Container == source.Container)
                    .ToList();

                InventoryIndex.ReplaceSource(
                    source,
                    sourceSnapshots);
            }
        }

        if (storageReader.TryReadActiveFreeCompany(
                observedAtUtc,
                out var freeCompanySources,
                out var freeCompanySnapshots))
        {
            foreach (var source in freeCompanySources)
            {
                var sourceSnapshots = freeCompanySnapshots
                    .Where(x =>
                        x.Storage == source.Storage &&
                        x.OwnerId == source.OwnerId &&
                        x.Container == source.Container)
                    .ToList();

                InventoryIndex.ReplaceSource(
                    source,
                    sourceSnapshots);
            }
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
        if (InventoryIndex.IsDirty)
        {
            InventoryIndex.SaveToDisk(
                InventoryIndexFilePath);

            Log.Information(
                $"Indice inventario salvato: {InventoryIndexFilePath} ({InventoryIndex.Items.Count} snapshot)");
        }

        if (CharacterCatalog.IsDirty)
        {
            CharacterCatalog.SaveToDisk(
                CharacterCatalogFilePath);

            Log.Information(
                $"Catalogo personaggi salvato: {CharacterCatalogFilePath} ({CharacterCatalog.Entries.Count} identità)");
        }
    }
}

internal sealed class FrogInventoryStartup : IHostedService
{
    private readonly IInventoryMonitor inventoryMonitor;
    private readonly IInventoryScanner inventoryScanner;
    private readonly StorageReaderAPI storageReader;
    private readonly Plugin plugin;

    public FrogInventoryStartup(
        IInventoryMonitor inventoryMonitor,
        IInventoryScanner inventoryScanner,
        StorageReaderAPI storageReader,
        Plugin plugin)
    {
        this.inventoryMonitor = inventoryMonitor;
        this.inventoryScanner = inventoryScanner;
        this.storageReader = storageReader;
        this.plugin = plugin;
    }

    public Task StartAsync(
        CancellationToken cancellationToken)
    {
        inventoryMonitor.OnInventoryChanged += OnInventoryChanged;

        inventoryMonitor.Start();

        inventoryScanner.Enable();

        inventoryMonitor.SignalRefresh();

        return Task.CompletedTask;
    }

    public Task StopAsync(
        CancellationToken cancellationToken)
    {
        inventoryMonitor.OnInventoryChanged -= OnInventoryChanged;

        return Task.CompletedTask;
    }

    private void OnInventoryChanged(
        List<InventoryChange> inventoryChanges,
        InventoryMonitor.ItemChanges? itemChanges)
    {
        plugin.SyncPlayerInventory();
        plugin.SyncStorageSources(storageReader);
    }
}
