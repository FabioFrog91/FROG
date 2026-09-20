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
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
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
    internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;

    [PluginService]
    internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/frog";
    private const string InventoryIndexFileName = "inventory-index.json";
    private const string CharacterCatalogFileName = "character-catalog.json";

    private const int LoginRetryIntervalMilliseconds = 500;
    private const int LoginRetryMaxAttempts = 10;

    private CancellationTokenSource? loginSyncCancellation;

    private readonly object freeCompanySyncDiagnosticsLock = new();
    private bool isFreeCompanyChestOpen;
    private FreeCompanySyncDiagnosticsSnapshot freeCompanySyncDiagnostics =
        FreeCompanySyncDiagnosticsSnapshot.Empty;
    private readonly List<FreeCompanySyncDiagnosticsSnapshot> freeCompanySyncHistory = new();
    private readonly Dictionary<(ulong OwnerId, uint Container), PendingFreeCompanyObservation>
        pendingFreeCompanyObservations = new();

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

    internal FreeCompanySyncDiagnosticsSnapshot GetFreeCompanySyncDiagnostics()
    {
        lock (freeCompanySyncDiagnosticsLock)
        {
            return CloneFreeCompanySyncDiagnostics(
                freeCompanySyncDiagnostics);
        }
    }

    internal IReadOnlyList<FreeCompanySyncDiagnosticsSnapshot> GetFreeCompanySyncDiagnosticsHistory()
    {
        lock (freeCompanySyncDiagnosticsLock)
        {
            return freeCompanySyncHistory
                .Select(CloneFreeCompanySyncDiagnostics)
                .ToList();
        }
    }

    private static FreeCompanySyncDiagnosticsSnapshot CloneFreeCompanySyncDiagnostics(
        FreeCompanySyncDiagnosticsSnapshot snapshot) =>
        snapshot with
        {
            Pages = snapshot.Pages
                .Select(page =>
                    page with
                    {
                        ChangedItems = page.ChangedItems.ToArray()
                    })
                .ToArray()
        };

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

        var chestOpen =
            isFreeCompanyChestOpen;

        var freeCompanyReadSucceeded = false;
        IReadOnlyList<InventorySource> freeCompanySources =
            Array.Empty<InventorySource>();
        IReadOnlyList<InventoryItemSnapshot> freeCompanySnapshots =
            Array.Empty<InventoryItemSnapshot>();

        var pageDiagnostics =
            new List<FreeCompanyPageSyncDiagnostic>();

        // A Free Company container can report IsLoaded while still holding
        // stale or transitional data from a different tab. Treat only the
        // tab that is actually selected in the chest UI as Observed.
        // Every other page remains Known at its last verified snapshot.
        if (chestOpen &&
            TryGetObservedFreeCompanyPage(
                out var observedFreeCompanyContainer) &&
            storageReader.TryReadActiveFreeCompanyPage(
                observedAtUtc,
                observedFreeCompanyContainer,
                out var freeCompanySource,
                out freeCompanySnapshots))
        {
            freeCompanyReadSucceeded = true;
            freeCompanySources =
                new[]
                {
                    freeCompanySource
                };

            var beforeSnapshots =
                InventoryIndex.Items
                    .Where(x =>
                        x.Storage == freeCompanySource.Storage &&
                        x.OwnerId == freeCompanySource.OwnerId &&
                        x.Container == freeCompanySource.Container)
                    .ToList();

            var shouldApplyObservation =
                ShouldApplyFreeCompanyObservation(
                    freeCompanySource,
                    beforeSnapshots,
                    freeCompanySnapshots,
                    observedAtUtc);

            if (shouldApplyObservation)
            {
                InventoryIndex.ReplaceSource(
                    freeCompanySource,
                    freeCompanySnapshots);
            }

            var afterSnapshots =
                InventoryIndex.Items
                    .Where(x =>
                        x.Storage == freeCompanySource.Storage &&
                        x.OwnerId == freeCompanySource.OwnerId &&
                        x.Container == freeCompanySource.Container)
                    .ToList();

            var beforeByItem =
                beforeSnapshots
                    .GroupBy(x =>
                        new
                        {
                            x.BaseItemId,
                            x.IsHq
                        })
                    .ToDictionary(
                        group =>
                            (group.Key.BaseItemId, group.Key.IsHq),
                        group =>
                            group.Sum(x => x.Quantity));

            var readByItem =
                freeCompanySnapshots
                    .GroupBy(x =>
                        new
                        {
                            x.BaseItemId,
                            x.IsHq
                        })
                    .ToDictionary(
                        group =>
                            (group.Key.BaseItemId, group.Key.IsHq),
                        group =>
                            group.Sum(x => x.Quantity));

            var changedItems =
                beforeByItem.Keys
                    .Union(readByItem.Keys)
                    .Select(key =>
                        new FreeCompanyItemSyncDiagnostic(
                            key.BaseItemId,
                            key.IsHq,
                            beforeByItem.TryGetValue(
                                key,
                                out var beforeQuantity)
                                ? beforeQuantity
                                : 0,
                            readByItem.TryGetValue(
                                key,
                                out var readQuantity)
                                ? readQuantity
                                : 0))
                    .Where(item =>
                        item.BeforeQuantity != item.ReadQuantity)
                    .OrderBy(item =>
                        item.BaseItemId)
                    .ThenBy(item =>
                        item.IsHq)
                    .ToList();

            pageDiagnostics.Add(
                new FreeCompanyPageSyncDiagnostic(
                    freeCompanySource.OwnerId,
                    freeCompanySource.Container,
                    beforeSnapshots.Count,
                    beforeSnapshots.Sum(x => x.Quantity),
                    freeCompanySnapshots.Count,
                    freeCompanySnapshots.Sum(x => x.Quantity),
                    afterSnapshots.Count,
                    afterSnapshots.Sum(x => x.Quantity),
                    changedItems));
        }

        lock (freeCompanySyncDiagnosticsLock)
        {
            freeCompanySyncDiagnostics =
                new FreeCompanySyncDiagnosticsSnapshot(
                    observedAtUtc,
                    chestOpen,
                    freeCompanyReadSucceeded,
                    freeCompanySources.Count,
                    freeCompanySnapshots.Count,
                    freeCompanySnapshots.Sum(x => x.Quantity),
                    pageDiagnostics);

            freeCompanySyncHistory.Add(
                CloneFreeCompanySyncDiagnostics(
                    freeCompanySyncDiagnostics));

            const int maxFreeCompanySyncHistory = 20;

            if (freeCompanySyncHistory.Count > maxFreeCompanySyncHistory)
            {
                freeCompanySyncHistory.RemoveRange(
                    0,
                    freeCompanySyncHistory.Count - maxFreeCompanySyncHistory);
            }
        }
    }

    private bool ShouldApplyFreeCompanyObservation(
        InventorySource source,
        IReadOnlyList<InventoryItemSnapshot> beforeSnapshots,
        IReadOnlyList<InventoryItemSnapshot> observedSnapshots,
        DateTime observedAtUtc)
    {
        var key =
            (source.OwnerId, source.Container);

        var beforeQuantity =
            beforeSnapshots.Sum(item =>
                item.Quantity);

        var observedQuantity =
            observedSnapshots.Sum(item =>
                item.Quantity);

        var isDestructiveObservation =
            observedSnapshots.Count < beforeSnapshots.Count ||
            observedQuantity < beforeQuantity;

        if (!isDestructiveObservation)
        {
            pendingFreeCompanyObservations.Remove(key);
            return true;
        }

        var fingerprint =
            BuildFreeCompanyObservationFingerprint(
                observedSnapshots);

        if (!pendingFreeCompanyObservations.TryGetValue(
                key,
                out var pending) ||
            !string.Equals(
                pending.Fingerprint,
                fingerprint,
                StringComparison.Ordinal))
        {
            pendingFreeCompanyObservations[key] =
                new PendingFreeCompanyObservation(
                    fingerprint,
                    observedAtUtc);

            return false;
        }

        // Require the same destructive snapshot to survive long enough to
        // distinguish a real removal from the transient empty/partial state
        // seen while the FC client is switching tabs.
        if (observedAtUtc - pending.FirstObservedAtUtc <
            TimeSpan.FromMilliseconds(400))
        {
            return false;
        }

        pendingFreeCompanyObservations.Remove(key);
        return true;
    }

    private static string BuildFreeCompanyObservationFingerprint(
        IReadOnlyList<InventoryItemSnapshot> snapshots) =>
        string.Join(
            "|",
            snapshots
                .OrderBy(item =>
                    item.Container)
                .ThenBy(item =>
                    item.Slot)
                .ThenBy(item =>
                    item.BaseItemId)
                .ThenBy(item =>
                    item.IsHq)
                .Select(item =>
                    $"{item.Container}:{item.Slot}:{item.BaseItemId}:{item.IsHq}:{item.Quantity}"));

    private static unsafe bool TryGetObservedFreeCompanyPage(
        out uint container)
    {
        container = 0;

        var addon =
            GameGui.GetAddonByName<AtkUnitBase>(
                "FreeCompanyChest",
                1);

        if (addon == null ||
            !addon->IsVisible)
        {
            return false;
        }

        if (IsFreeCompanyTabSelected(addon, 101))
        {
            container =
                (uint)InventoryType.FreeCompanyPage1;

            return true;
        }

        if (IsFreeCompanyTabSelected(addon, 100))
        {
            container =
                (uint)InventoryType.FreeCompanyPage2;

            return true;
        }

        if (IsFreeCompanyTabSelected(addon, 99))
        {
            container =
                (uint)InventoryType.FreeCompanyPage3;

            return true;
        }

        if (IsFreeCompanyTabSelected(addon, 98))
        {
            container =
                (uint)InventoryType.FreeCompanyPage4;

            return true;
        }

        if (IsFreeCompanyTabSelected(addon, 97))
        {
            container =
                (uint)InventoryType.FreeCompanyPage5;

            return true;
        }

        return false;
    }

    private static unsafe bool IsFreeCompanyTabSelected(
        AtkUnitBase* addon,
        uint nodeId)
    {
        if (nodeId >= addon->UldManager.NodeListCount)
            return false;

        var node =
            addon->UldManager.NodeList[nodeId];

        if (node == null ||
            !node->IsVisible())
        {
            return false;
        }

        var componentNode =
            node->GetAsAtkComponentNode();

        if (componentNode == null ||
            componentNode->Component == null)
        {
            return false;
        }

        var component =
            componentNode->Component;

        if (component->UldManager.NodeListCount > 2)
        {
            var checkMark =
                component->UldManager.NodeList[2];

            if (checkMark != null &&
                checkMark->IsVisible())
            {
                return true;
            }
        }

        var radioButton =
            (AtkComponentRadioButton*)component;

        return (radioButton->Flags & 0x40000) != 0;
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
        isFreeCompanyChestOpen = true;
        pendingFreeCompanyObservations.Clear();

        SaveInventoryIndex();
    }

    private void OnFreeCompanyChestClosed(
        AddonEvent type,
        AddonArgs args)
    {
        isFreeCompanyChestOpen = false;
        pendingFreeCompanyObservations.Clear();

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

internal sealed record PendingFreeCompanyObservation(
    string Fingerprint,
    DateTime FirstObservedAtUtc);

internal sealed record FreeCompanyItemSyncDiagnostic(
    uint BaseItemId,
    bool IsHq,
    int BeforeQuantity,
    int ReadQuantity);

internal sealed record FreeCompanyPageSyncDiagnostic(
    ulong FreeCompanyId,
    uint Container,
    int BeforeSnapshotCount,
    int BeforeQuantity,
    int ReadSnapshotCount,
    int ReadQuantity,
    int AfterSnapshotCount,
    int AfterQuantity,
    IReadOnlyList<FreeCompanyItemSyncDiagnostic> ChangedItems);

internal sealed record FreeCompanySyncDiagnosticsSnapshot(
    DateTime? ObservedAtUtc,
    bool ChestOpen,
    bool ReadSucceeded,
    int SourceCount,
    int SnapshotCount,
    int TotalQuantity,
    IReadOnlyList<FreeCompanyPageSyncDiagnostic> Pages)
{
    public static FreeCompanySyncDiagnosticsSnapshot Empty { get; } =
        new(
            null,
            false,
            false,
            0,
            0,
            0,
            Array.Empty<FreeCompanyPageSyncDiagnostic>());
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
