using CriticalCommonLib.Services;
using Dalamud.Bindings.ImGui;
using FROG.Core.Inventory;

namespace FROG.Windows;

internal sealed class MainWindowWithFcDebug : MainWindow
{
    public MainWindowWithFcDebug(
        Plugin plugin,
        ICharacterMonitor characterMonitor,
        CharacterCatalog characterCatalog,
        RetainerDisplayLocator retainerDisplayLocator,
        ResolverCoordinator resolverCoordinator,
        GlobalPlannerCoordinator globalPlannerCoordinator,
        PlanExecutionRuntime executionRuntime,
        ExecutionWindow executionWindow)
        : base(
            plugin,
            characterMonitor,
            characterCatalog,
            retainerDisplayLocator,
            resolverCoordinator,
            globalPlannerCoordinator,
            executionRuntime,
            executionWindow)
    {
    }

    public override void Draw()
    {
        base.Draw();

        ImGui.Spacing();

        if (ImGui.CollapsingHeader("DEBUG FC RAM"))
        {
            FreeCompanyObservationDiagnosticsPanel.Draw();
        }
    }
}
