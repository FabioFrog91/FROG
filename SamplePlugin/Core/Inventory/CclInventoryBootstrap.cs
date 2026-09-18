using AllaganLib.GameSheets.Modules;
using AllaganLib.Monitors.Interfaces;
using AllaganLib.Monitors.Services;
using Autofac;
using CriticalCommonLib.Services;
using DalaMock.Shared.Extensions;
using Lumina;

namespace FROG.Core.Inventory;

public static class CclInventoryBootstrap
{
    public static void Register(ContainerBuilder builder)
    {
        builder.RegisterModule(new GameSheetManagerModule());
        builder.RegisterModule(new GameDataModule());

        builder.Register(c => c.Resolve<Dalamud.Plugin.Services.IDataManager>().GameData)
            .As<GameData>()
            .SingleInstance()
            .ExternallyOwned();

        builder.RegisterType<FrogAchievementMonitorConfiguration>()
            .As<IAchievementMonitorConfiguration>()
            .SingleInstance();

        builder.RegisterTransientSelfAndInterfaces<CriticalCommonLib.Models.Character>();
        builder.RegisterTransientSelfAndInterfaces<CriticalCommonLib.Models.Inventory>();
        builder.RegisterTransientSelfAndInterfaces<CriticalCommonLib.Models.InventoryChange>();
        builder.RegisterTransientSelfAndInterfaces<CriticalCommonLib.Models.InventoryItem>();

        builder.RegisterSingletonSelfAndInterfaces<CharacterMonitor>();
        builder.RegisterSingletonSelfAndInterfaces<GameInterface>();
        builder.RegisterSingletonSelfAndInterfaces<GameUiManager>();
        builder.RegisterSingletonSelfAndInterfaces<MarketOrderService>();
        builder.RegisterSingletonSelfAndInterfaces<InventoryScanner>();
        builder.RegisterSingletonSelfAndInterfaces<InventoryMonitor>();
    }
}

internal sealed class FrogAchievementMonitorConfiguration : IAchievementMonitorConfiguration
{
    public int PollIntervalSeconds { get; set; } = 5;
}
