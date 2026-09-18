using AllaganLib.Monitors.Services;
using Autofac;
using CriticalCommonLib.Services;
using DalaMock.Host.Hosting;
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
    internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/frog";

    public Configuration Configuration { get; private set; } = null!;

    public InventoryIndex InventoryIndex { get; } = new();

    public WindowSystem WindowSystem { get; } = new("FROG");

    private ConfigWindow ConfigWindow { get; }
    private MainWindow MainWindow { get; }

    public Plugin(IDalamudPluginInterface pluginInterface)
        : base(pluginInterface)
    {
        Configuration =
            pluginInterface.GetPluginConfig() as Configuration
            ?? new Configuration();

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Apre l'interfaccia principale di FROG"
        });

        pluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        pluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        pluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        Log.Information($"=== {pluginInterface.Manifest.Name} avviato ===");
    }

    public override HostedPluginOptions ConfigureOptions()
    {
        return new HostedPluginOptions
        {
            UseMediatorService = false,
        };
    }

    public override void ConfigureContainer(ContainerBuilder containerBuilder)
    {
        CclInventoryBootstrap.Register(containerBuilder);

        RegisterHostedService(typeof(AchievementMonitorService));
        RegisterHostedService(typeof(OdrScanner));
        RegisterHostedService(typeof(FrogInventoryStartup));
    }

    public override void ConfigureServices(IServiceCollection serviceCollection)
    {
    }

    public override void Dispose()
    {
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
}

internal sealed class FrogInventoryStartup : IHostedService
{
    private readonly IInventoryMonitor inventoryMonitor;
    private readonly IInventoryScanner inventoryScanner;

    public FrogInventoryStartup(
        IInventoryMonitor inventoryMonitor,
        IInventoryScanner inventoryScanner)
    {
        this.inventoryMonitor = inventoryMonitor;
        this.inventoryScanner = inventoryScanner;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        inventoryMonitor.Start();
        inventoryScanner.Enable();

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
