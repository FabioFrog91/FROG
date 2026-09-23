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
    internal static IGameGui GameGui { get; private set; } = null!;

    [PluginService]
    internal static IFramework Framework { get; private set; } = null!;

    [PluginService]
    internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;

    [PluginService]
    internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/frog";
    private const string InventoryIndexFileName = "inventory-index.json";
    private const string CharacterCatalogFileName = "character-catalog.json";

    private const int LoginRetryIntervalMilliseconds = 500;
    private const int LoginRetryMaxAttempts = 10;

    private readonly object loginSyncLock = new();
    private CancellationTokenSource? loginSyncCancellation;
    private bool disposed;

    private readonly PlayerInventoryAPI playerInventory;

    private CharacterCatalogSync? characterCatalogSync;

    public Configuration Configuration { get; private set; } = null!;

    public InventoryIndex InventoryIndex { get; } = new();

    public CharacterCatalog CharacterCatalog { get; } = new();

    public WindowSystem WindowSystem { get; } = new("FROG");

    private ConfigWindow ConfigWindow { get; }
    private MainWindow? MainWindow { get; set; }
    private ExecutionWindow? ExecutionWindow { get; set; }
    private ExecutionQuantityOverlay? ExecutionQuantityOverlay { get; set; }

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
            .RegisterInstance(CharacterCatalog)
            .AsSelf()
            .SingleInstance();

        containerBuilder
            .RegisterType<PlannerSourceBuilder>()
            .AsSelf()
            .SingleInstance();

        containerBuilder
            .RegisterType<ResolverCoordinator>()
            .AsSelf()
            .SingleInstance();

        containerBuilder
            .RegisterType<GlobalPlannerCoordinator>()
            .AsSelf()
            .SingleInstance();

        containerBuilder
            .RegisterType<PlanExecutionPlanGuard>()
            .AsSelf()
            .SingleInstance();

        containerBuilder
            .RegisterType<PlanExecutionRuntime>()
            .AsSelf()
            .SingleInstance();

        containerBuilder
            .RegisterType<RetainerListHighlighter>()
            .AsSelf()
            .SingleInstance();

        containerBuilder
            .RegisterType<ExecutionInventoryHighlighter>()
            .AsSelf()
            .SingleInstance();

        containerBuilder
            .RegisterType<ExecutionQuantityOverlay>()
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

        var retainerDisplayLocator =
            Host.Services.GetRequiredService<RetainerDisplayLocator>();

        var resolverCoordinator =
            Host.Services.GetRequiredService<ResolverCoordinator>();

        var globalPlannerCoordinator =
            Host.Services.GetRequiredService<GlobalPlannerCoordinator>();

        var executionRuntime =
            Host.Services.GetRequiredService<PlanExecutionRuntime>();

        ExecutionQuantityOverlay =
            Host.Services.GetRequiredService<ExecutionQuantityOverlay>();

        PluginInterface.UiBuilder.Draw +=
            ExecutionQuantityOverlay.Draw;

        characterCatalogSync =
            new CharacterCatalogSync(
                characterMonitor,
                CharacterCatalog);

        characterCatalogSync.SyncAll();

        characterMonitor.OnCharacterUpdated +=
            OnCharacterUpdated;

        ExecutionWindow =
            new ExecutionWindow(
                executionRuntime,
                retainerDisplayLocator,
                characterMonitor,
                CharacterCatalog,
                this);

        MainWindow =
            new MainWindowWithFcDebug(
                this,
                characterMonitor,
                CharacterCatalog,
                retainerDisplayLocator,
                resolverCoordinator,
                globalPlannerCoordinator,
                executionRuntime,
                ExecutionWindow);

        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(ExecutionWindow);

        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        if (disposed)
            return;

        disposed = true;

        try
        {
            RunDisposeStep(
                "cancellazione sincronizzazione login",
                CancelLoginSyncRetry);

            RunDisposeStep(
                "cancellazione planner globale",
                () =>
                {
                    if (Host != null)
                    {
                        Host.Services
                            .GetService<GlobalPlannerCoordinator>()?
                            .Dispose();
                    }
                });

            RunDisposeStep(
                "evento login",
                () => ClientState.Login -= OnLogin);

            RunDisposeStep(
                "evento logout",
                () => ClientState.Logout -= OnLogout);

            RunDisposeStep(
                "evento monitor personaggio",
                () =>
                {
                    var characterMonitor =
                        Host != null
                            ? Host.Services.GetService<ICharacterMonitor>()
                            : null;

                    if (characterMonitor != null)
                    {
                        characterMonitor.OnCharacterUpdated -=
                            OnCharacterUpdated;
                    }
                });

            RunDisposeStep(
                "sincronizzazione catalogo personaggi",
                () => characterCatalogSync?.SyncAll());

            RunDisposeStep(
                "apertura RetainerList",
                () => AddonLifecycle.UnregisterListener(
                    AddonEvent.PostSetup,
                    "RetainerList",
                    OnRetainerListOpened));

            RunDisposeStep(
                "chiusura RetainerList",
                () => AddonLifecycle.UnregisterListener(
                    AddonEvent.PreFinalize,
                    "RetainerList",
                    OnRetainerListClosed));

            RunDisposeStep(
                "apertura FreeCompanyChest",
                () => AddonLifecycle.UnregisterListener(
                    AddonEvent.PostSetup,
                    "FreeCompanyChest",
                    OnFreeCompanyChestOpened));

            RunDisposeStep(
                "chiusura FreeCompanyChest",
                () => AddonLifecycle.UnregisterListener(
                    AddonEvent.PreFinalize,
                    "FreeCompanyChest",
                    OnFreeCompanyChestClosed));

            RunDisposeStep(
                "salvataggio stato persistente",
                SaveInventoryIndex);

            RunDisposeStep(
                "overlay quantità",
                () =>
                {
                    if (ExecutionQuantityOverlay != null)
                    {
                        PluginInterface.UiBuilder.Draw -=
                            ExecutionQuantityOverlay.Draw;
                    }
                });

            RunDisposeStep(
                "disegno finestre",
                () => PluginInterface.UiBuilder.Draw -= WindowSystem.Draw);

            RunDisposeStep(
                "apertura configurazione",
                () => PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi);

            RunDisposeStep(
                "apertura finestra principale",
                () => PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi);

            RunDisposeStep(
                "rimozione finestre",
                WindowSystem.RemoveAllWindows);

            RunDisposeStep(
                "finestra configurazione",
                ConfigWindow.Dispose);

            RunDisposeStep(
                "finestra principale",
                () => MainWindow?.Dispose());

            RunDisposeStep(
                "finestra esecuzione",
                () => ExecutionWindow?.Dispose());

            RunDisposeStep(
                "comando plugin",
                () => CommandManager.RemoveHandler(CommandName));
        }
        finally
        {
            try
            {
                base.Dispose();
            }
            catch (Exception ex)
            {
                TryLogError(
                    ex,
                    "Errore durante il cleanup del plugin: host del plugin.");
            }
        }
    }

    private static void RunDisposeStep(
        string step,
        Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            TryLogError(
                ex,
                $"Errore durante il cleanup del plugin: {step}.");
        }
    }

    private static void TryLogError(
        Exception exception,
        string message)
    {
        try
        {
            Log.Error(
                exception,
                message);
        }
        catch
        {
            // Logging may already be unavailable while the plugin unloads.
        }
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
        CancelLoginSyncRetry();

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
        CancellationTokenSource? previousCancellation;

        lock (loginSyncLock)
        {
            if (disposed)
                return;

            previousCancellation =
                loginSyncCancellation;

            var cancellation =
                new CancellationTokenSource();

            loginSyncCancellation =
                cancellation;

            _ = RunLoginSyncRetryAsync(
                cancellation);
        }

        CancelSafely(
            previousCancellation);
    }

    private async Task RunLoginSyncRetryAsync(
        CancellationTokenSource cancellation)
    {
        var cancellationToken =
            cancellation.Token;

        try
        {
            for (var attempt = 1;
                 attempt <= LoginRetryMaxAttempts;
                 attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await Task.Delay(
                    LoginRetryIntervalMilliseconds,
                    cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();

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
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            // Expected when a new login starts, on logout or during unload.
        }
        catch (Exception ex)
        {
            TryLogError(
                ex,
                "Errore durante la sincronizzazione inventario dopo il login.");
        }
        finally
        {
            lock (loginSyncLock)
            {
                if (ReferenceEquals(
                        loginSyncCancellation,
                        cancellation))
                {
                    loginSyncCancellation = null;
                }
            }

            cancellation.Dispose();
        }
    }

    private void CancelLoginSyncRetry()
    {
        CancellationTokenSource? cancellation;

        lock (loginSyncLock)
        {
            cancellation =
                loginSyncCancellation;
        }

        CancelSafely(
            cancellation);
    }

    private static void CancelSafely(
        CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
            return;

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The retry completed between capture and cancellation.
        }
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
            allSnapshots,
            observedAtUtc);

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
                    sourceSnapshots,
                    observedAtUtc);
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
        FreeCompanyObservationDiagnostics.Add(
            "CHEST OPEN");

        SaveInventoryIndex();
    }

    private void OnFreeCompanyChestClosed(
        AddonEvent type,
        AddonArgs args)
    {
        FreeCompanyObservationDiagnostics.Add(
            "CHEST CLOSE");

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
                $"Catalogo personaggi salvato da disco: {CharacterCatalogFilePath} ({CharacterCatalog.Entries.Count} identità)");
        }
    }
}

internal sealed class FrogInventoryStartup : IHostedService
{
    private readonly IInventoryMonitor inventoryMonitor;
    private readonly IInventoryScanner inventoryScanner;
    private readonly StorageReaderAPI storageReader;
    private readonly Plugin plugin;
    private readonly PlanExecutionRuntime executionRuntime;
    private readonly RetainerListHighlighter retainerListHighlighter;
    private readonly ExecutionInventoryHighlighter executionInventoryHighlighter;

    public FrogInventoryStartup(
        IInventoryMonitor inventoryMonitor,
        IInventoryScanner inventoryScanner,
        StorageReaderAPI storageReader,
        Plugin plugin,
        PlanExecutionRuntime executionRuntime,
        RetainerListHighlighter retainerListHighlighter,
        ExecutionInventoryHighlighter executionInventoryHighlighter)
    {
        this.inventoryMonitor = inventoryMonitor;
        this.inventoryScanner = inventoryScanner;
        this.storageReader = storageReader;
        this.plugin = plugin;
        this.executionRuntime = executionRuntime;
        this.retainerListHighlighter = retainerListHighlighter;
        this.executionInventoryHighlighter = executionInventoryHighlighter;
    }

    public Task StartAsync(
        CancellationToken cancellationToken)
    {
        inventoryMonitor.OnInventoryChanged += OnInventoryChanged;
        Plugin.Framework.Update += OnFrameworkUpdate;

        inventoryMonitor.Start();

        inventoryScanner.Enable();

        inventoryMonitor.SignalRefresh();

        return Task.CompletedTask;
    }

    public Task StopAsync(
        CancellationToken cancellationToken)
    {
        inventoryMonitor.OnInventoryChanged -= OnInventoryChanged;
        Plugin.Framework.Update -= OnFrameworkUpdate;

        retainerListHighlighter.Clear();
        executionInventoryHighlighter.Clear();

        return Task.CompletedTask;
    }

    private void OnFrameworkUpdate(
        IFramework framework)
    {
        var currentCharacterId =
            Plugin.PlayerState.IsLoaded
                ? Plugin.PlayerState.ContentId
                : 0;

        executionRuntime.Update(
            currentCharacterId);

        retainerListHighlighter.Update(
            currentCharacterId);

        executionInventoryHighlighter.Update(
            currentCharacterId);
    }

    private void OnInventoryChanged(
        List<InventoryChange> inventoryChanges,
        InventoryMonitor.ItemChanges? itemChanges)
    {
        plugin.SyncStorageSources(storageReader);
    }
}
