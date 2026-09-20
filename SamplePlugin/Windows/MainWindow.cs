using CriticalCommonLib.Models;
using CriticalCommonLib.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Inventory;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using FROG.Core.Inventory;
using FROG.Core.Inventory.Providers;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;

namespace FROG.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly ICharacterMonitor characterMonitor;
    private readonly CharacterCatalog characterCatalog;
    private readonly RetainerDisplayLocator retainerDisplayLocator;
    private readonly ResolverCoordinator resolverCoordinator;
    private readonly GlobalPlannerCoordinator globalPlannerCoordinator;
    private readonly PlanExecutionRuntime executionRuntime;
    private readonly ExecutionWindow executionWindow;

    private RequirementSet? importedRequirementSet;
    private readonly OptimizationSettings optimizationSettings = new();

    private int searchItemId;

    public MainWindow(
        Plugin plugin,
        ICharacterMonitor characterMonitor,
        CharacterCatalog characterCatalog,
        RetainerDisplayLocator retainerDisplayLocator,
        ResolverCoordinator resolverCoordinator,
        GlobalPlannerCoordinator globalPlannerCoordinator,
        PlanExecutionRuntime executionRuntime,
        ExecutionWindow executionWindow)
        : base("FROG")
    {
        this.plugin = plugin;
        this.characterMonitor = characterMonitor;
        this.characterCatalog = characterCatalog;
        this.retainerDisplayLocator = retainerDisplayLocator;
        this.resolverCoordinator = resolverCoordinator;
        this.globalPlannerCoordinator = globalPlannerCoordinator;
        this.executionRuntime = executionRuntime;
        this.executionWindow = executionWindow;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(500, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        ImGui.Text("FROG");
        ImGui.Separator();

        DrawBuildInfo();

        ImGui.Spacing();

        var playerState = Plugin.PlayerState;

        if (!playerState.IsLoaded)
        {
            ImGui.Text("Personaggio non disponibile.");
            return;
        }

        if (ImGui.CollapsingHeader(
                "PERSONAGGIO",
                ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Text($"Nome: {playerState.CharacterName}");
            ImGui.Text($"Livello: {playerState.Level}");

            if (playerState.ClassJob.IsValid)
            {
                ImGui.Text(
                    $"Job: {playerState.ClassJob.Value.Abbreviation}");
            }

            ImGui.Text($"Content ID: {playerState.ContentId:X}");
        }

        ImGui.Spacing();

        DrawTeamcraftImport();

        ImGui.Spacing();

        DrawResolverDiagnostics();

        ImGui.Spacing();

        if (ImGui.CollapsingHeader("CLIENT"))
        {
            DrawClientDiagnostics();
        }

        ImGui.Spacing();

        if (ImGui.CollapsingHeader("INVENTARIO"))
        {
            DrawInventoryDiagnostics();
        }

        ImGui.Spacing();

        if (ImGui.CollapsingHeader("STORAGE INDEX"))
        {
            DrawStorageIndexDiagnostics();
        }

        ImGui.Spacing();

        if (ImGui.CollapsingHeader("DEBUG SYNC RAM"))
        {
            DrawSyncDiagnostics();
        }

        ImGui.Spacing();

        if (ImGui.CollapsingHeader("RICERCA NELL'INDICE"))
        {
            DrawIndexSearch();
        }
    }

    private void DrawTeamcraftImport()
    {
        ImGui.Text("TEAMCRAFT IMPORT");
        ImGui.Separator();

        ImGui.TextWrapped(
            "Copia una lista Teamcraft negli appunti e importala direttamente in FROG.");

        if (ImGui.Button("Importa da Clipboard"))
        {
            var text = ImGui.GetClipboardText();

            var importer =
                new TeamcraftListImporter(
                    Plugin.DataManager);

            importedRequirementSet =
                importer.Import(text);

            resolverCoordinator.Invalidate(
                resetDiagnostics: true);

            globalPlannerCoordinator.ClearResult();
            ResetPlanExecutionSession();
        }

        if (importedRequirementSet == null)
        {
            ImGui.Text("Nessuna lista importata.");
            return;
        }

        ImGui.Spacing();

        ImGui.Text(
            $"Requirement importati: {importedRequirementSet.Requirements.Count}");

        foreach (var requirement in importedRequirementSet.Requirements)
        {
            var quality =
                requirement.QualityPolicy.ToString();

            ImGui.Text(
                $"{GetItemName(requirement.BaseItemId)} | " +
                $"Qty {requirement.Quantity} | " +
                $"Quality {quality} | " +
                $"Precraft {requirement.IsPrecraft}");
        }
    }

    private void DrawResolverDiagnostics()
    {
        ImGui.Text("RESOLVER / TRANSFER PLAN");
        ImGui.Separator();

        if (importedRequirementSet == null)
        {
            ImGui.Text(
                "Importa prima una lista Teamcraft.");

            return;
        }

        if (importedRequirementSet.Requirements.Count == 0)
        {
            ImGui.Text(
                "La lista importata non contiene Requirement.");

            return;
        }

        if (plugin.InventoryIndex.Count == 0)
        {
            ImGui.Text(
                "Inventory Index vuoto. Nessun Requirement può essere risolto.");

            return;
        }

        var currentCharacterId =
            Plugin.PlayerState.IsLoaded
                ? Plugin.PlayerState.ContentId
                : 0;

        if (currentCharacterId == 0)
        {
            ImGui.Text(
                "Personaggio principale non disponibile.");

            return;
        }

        var resolverState =
            resolverCoordinator.Resolve(
                importedRequirementSet,
                currentCharacterId,
                plugin.InventoryIndex,
                plugin.LastSyncAtUtc);

        var sources =
            resolverState.Sources;

        var sourceCatalog =
            resolverState.SourceCatalog;

        var resolutionPolicy =
            resolverState.ResolutionPolicy;

        var plan =
            resolverState.Plan;

        if (sources == null ||
            sourceCatalog == null ||
            resolutionPolicy == null ||
            plan == null)
        {
            ImGui.Text(
                "Nessuna InventorySource disponibile per il planner.");

            return;
        }

        ImGui.Text(
            $"Source disponibili: {sources.Count}");

        ImGui.Text(
            $"Requirement: {importedRequirementSet.Requirements.Count}");

        ImGui.Text(
            $"Intent: {plan.Intents.Count}");

        ImGui.Text(
            $"Mancante: {plan.Missing}");

        ImGui.Text(
            $"Piano completo: {(plan.IsComplete ? "SI" : "NO")}");

        ImGui.Spacing();

        ImGui.Text("DIAGNOSTICA RESOLVER CACHE");
        ImGui.Separator();

        ImGui.Text(
            $"Chiamate compute reali: {resolverState.ComputeCalls:N0}");

        ImGui.Text(
            $"Ultimo compute: {resolverState.LastElapsedMilliseconds:N3} ms");

        ImGui.Text(
            $"Allocato ultimo compute: {FormatMegabytes(resolverState.LastAllocatedBytes):N3} MB");

        ImGui.Text(
            $"Picco allocato per compute: {FormatMegabytes(resolverState.PeakAllocatedBytes):N3} MB");

        ImGui.Text(
            $"Allocato cumulativo resolver: {FormatMegabytes(resolverState.TotalAllocatedBytes):N1} MB");

        ImGui.Spacing();

        if (ImGui.Button("COPIA DIAGNOSTICA RESOLVER"))
        {
            ImGui.SetClipboardText(
                BuildResolverRuntimeDiagnosticsClipboardText(
                    importedRequirementSet,
                    resolutionPolicy,
                    resolverState));
        }

        ImGui.Spacing();

        if (ImGui.Button("COPIA TUTTO RESOLVER"))
        {
            ImGui.SetClipboardText(
                BuildResolverClipboardText(
                    importedRequirementSet,
                    sourceCatalog,
                    resolutionPolicy,
                    plan));
        }

        ImGui.SameLine();

        if (ImGui.Button("COPIA ORDINE PRIORITÀ"))
        {
            ImGui.SetClipboardText(
                BuildPriorityClipboardText(
                    resolutionPolicy,
                    sourceCatalog));
        }

        ImGui.SameLine();

        if (ImGui.Button("COPIA RESOLUTION"))
        {
            ImGui.SetClipboardText(
                BuildResolutionClipboardText(
                    importedRequirementSet,
                    plan));
        }

        ImGui.SameLine();

        if (ImGui.Button("COPIA TRANSFER INTENTS"))
        {
            ImGui.SetClipboardText(
                BuildTransferIntentsClipboardText(plan));
        }

        ImGui.Spacing();

        DrawGlobalPlannerDiagnostics(
            importedRequirementSet,
            resolutionPolicy);

        ImGui.Spacing();

        if (ImGui.CollapsingHeader(
                "ORDINE PRIORITÀ",
                ImGuiTreeNodeFlags.DefaultOpen))
        {
            for (var i = 0;
                 i < resolutionPolicy.Sources.Count;
                 i++)
            {
                var source =
                    resolutionPolicy.Sources[i];

                var sourcePolicy =
                    sourceCatalog.Sources
                        .FirstOrDefault(x => x.Source == source);

                var read =
                    sourcePolicy?.Read == true
                        ? "YES"
                        : "NO";

                var use =
                    sourcePolicy?.Use == true
                        ? "YES"
                        : "NO";

                ImGui.Text(
                    $"{i + 1}. " +
                    $"{GetSourceName(source)} | " +
                    $"{GetContainerName(source)} | " +
                    $"READ={read} | USE={use}");
            }
        }

        ImGui.Spacing();

        if (ImGui.CollapsingHeader(
                "RESOLUTION",
                ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (var resolution in plan.Resolutions)
            {
                ImGui.Text(
                    $"{GetItemName(resolution.Requirement.BaseItemId)} | " +
                    $"Richiesto {resolution.Requirement.Quantity} | " +
                    $"Disponibile {resolution.Available} | " +
                    $"Mancante {resolution.Missing}");

                foreach (var allocation in resolution.Allocations)
                {
                    ImGui.Text(
                        $"  -> {GetSourceName(allocation.Source)} | " +
                        $"{GetContainerName(allocation.Source)} | " +
                        $"{(allocation.IsHq ? "HQ" : "NQ")} | " +
                        $"Qty {allocation.Quantity}");
                }

                ImGui.Spacing();
            }
        }

        ImGui.Spacing();

        if (ImGui.CollapsingHeader(
                "TRANSFER INTENTS",
                ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (plan.Intents.Count == 0)
            {
                ImGui.Text(
                    "Nessun TransferIntent generato.");
            }
            else
            {
                foreach (var intent in plan.Intents)
                {
                    ImGui.Text(
                        $"{GetItemName(intent.BaseItemId)} | " +
                        $"{GetSourceName(intent.Source)} | " +
                        $"{GetContainerName(intent.Source)} | " +
                        $"{(intent.IsHq ? "HQ" : "NQ")} | " +
                        $"Qty {intent.Quantity}");
                }
            }
        }
    }

    private void DrawGlobalPlannerDiagnostics(
        RequirementSet requirementSet,
        ResolutionPolicy resolutionPolicy)
    {
        var plannerState =
            globalPlannerCoordinator.Snapshot;

        ImGui.Text("GLOBAL TRANSFER PLANNER");
        ImGui.Separator();

        ImGui.TextWrapped(
            "Planner globale: risolve l'intera lista considerando inventario principale, retainer, FC e cambi personaggio.");

        if (!plannerState.IsRunning)
        {
            if (ImGui.Button("CALCOLA PIANO GLOBALE"))
            {
                StartGlobalPlannerTask(
                    requirementSet,
                    resolutionPolicy);

                plannerState =
                    globalPlannerCoordinator.Snapshot;
            }
        }
        else
        {
            ImGui.Text("Calcolo...");

            if (plannerState.Diagnostics != null)
            {
                var diagnostics =
                    plannerState.Diagnostics.Snapshot();

                DrawGlobalPlannerSearchDiagnostics(
                    diagnostics);

                if (ImGui.Button("COPIA DIAGNOSTICA LIVE"))
                {
                    ImGui.SetClipboardText(
                        BuildGlobalPlannerDiagnosticsClipboardText(
                            requirementSet,
                            diagnostics));
                }

                ImGui.Spacing();
            }
        }

        if (!string.IsNullOrWhiteSpace(
                plannerState.Error))
        {
            ImGui.TextWrapped(
                $"Errore planner: {plannerState.Error}");

            return;
        }

        var plan =
            plannerState.Plan;

        if (plan == null)
        {
            if (!plannerState.IsRunning)
            {
                ImGui.Text(
                    "Nessun piano globale calcolato.");
            }

            return;
        }

        ImGui.Text(
            $"Risultato: {plan.Result}");

        ImGui.Text(
            $"Mancante: {plan.Missing}");

        if (plannerState.ResolverMissingSnapshot.HasValue)
        {
            var delta =
                plan.Missing -
                plannerState.ResolverMissingSnapshot.Value;

            ImGui.Text(
                $"Resolver stesso snapshot: {plannerState.ResolverMissingSnapshot.Value}");

            ImGui.Text(
                $"Delta planner-resolver: {delta:+#;-#;0}");
        }

        ImGui.Text(
            $"Cambi personaggio: {plan.CharacterSwitches}");

        ImGui.Text(
            $"Accessi retainer: {plan.RetainerAccesses}");

        ImGui.Text(
            $"Hop logici: {plan.TransferHops}");

        ImGui.Text(
            $"Azioni: {plan.Actions.Count}");

        if (!string.IsNullOrWhiteSpace(
                plannerState.ReplanMessage))
        {
            ImGui.TextWrapped(
                plannerState.ReplanMessage);
        }

        if (plannerState.Diagnostics != null)
        {
            ImGui.Spacing();

            DrawGlobalPlannerSearchDiagnostics(
                plannerState.Diagnostics.Snapshot());
        }

        ImGui.Spacing();

        if (ImGui.Button("COPIA PIANO GLOBALE"))
        {
            ImGui.SetClipboardText(
                BuildGlobalPlannerClipboardText(
                    requirementSet,
                    plan));
        }

        ImGui.Spacing();

        DrawPlanExecutionSession(
            plan,
            requirementSet);

        plannerState =
            globalPlannerCoordinator.Snapshot;

        plan =
            plannerState.Plan;

        if (plan == null)
            return;

        ImGui.Spacing();

        foreach (var action in plan.Actions)
        {
            if (action.Type == PlannerActionType.SwitchCharacter)
            {
                ImGui.Text(
                    $"SWITCH {GetCharacterName(action.FromCharacterId)} " +
                    $"({action.FromCharacterId}) -> " +
                    $"{GetCharacterName(action.ToCharacterId)} " +
                    $"({action.ToCharacterId})");

                continue;
            }

            if (action.Source is null ||
                action.Destination is null)
            {
                continue;
            }

            ImGui.Text(
                $"{GetItemName(action.BaseItemId)} | " +
                $"{(action.IsHq ? "HQ" : "NQ")} | " +
                $"Qty {action.Quantity} | " +
                $"{GetSourceName(action.Source)} -> " +
                $"{GetSourceName(action.Destination)}");
        }
    }

    private void DrawPlanExecutionSession(
        PlannerPlan plan,
        RequirementSet requirementSet)
    {
        ImGui.Text("ESECUZIONE AUTOMATICA");
        ImGui.Separator();

        var execution =
            executionRuntime.Snapshot;

        var session =
            execution.Session;

        if (session == null ||
            !ReferenceEquals(
                session.Plan,
                plan))
        {
            ImGui.TextWrapped(
                "Avvia l'esecuzione guidata. FROG osserverà automaticamente inventario, retainer, FC e cambi personaggio e passerà all'azione successiva senza conferme manuali.");

            if (ImGui.Button("AVVIA ESECUZIONE"))
            {
                executionRuntime.Start(
                    plan,
                    requirementSet,
                    optimizationSettings);

                executionWindow.IsOpen = true;
            }

            return;
        }

        ImGui.Text(
            $"Verificate: {session.VerifiedActionCount}/{session.TotalActionCount}");

        ImGui.Text(
            $"Rimanenti: {session.RemainingActionCount}");

        if (execution.IsReplanning)
        {
            ImGui.TextWrapped(
                "Varianza osservata: FROG sta ricalcolando automaticamente il piano residuo.");
        }
        else if (session.IsComplete)
        {
            ImGui.Text(
                "Esecuzione completata.");
        }
        else
        {
            ImGui.TextWrapped(
                "La guida di esecuzione è attiva e si aggiorna automaticamente.");
        }

        ImGui.Spacing();

        if (ImGui.Button("APRI GUIDA ESECUZIONE"))
        {
            executionWindow.IsOpen = true;
        }

        ImGui.SameLine();

        if (ImGui.Button("INTERROMPI ESECUZIONE"))
        {
            executionRuntime.Clear();
            executionWindow.IsOpen = false;
        }
    }

    private void ResetPlanExecutionSession()
    {
        executionRuntime.Clear();
        executionWindow.IsOpen = false;
    }

    private void StartGlobalPlannerTask(
        RequirementSet requirementSet,
        ResolutionPolicy resolutionPolicy)
    {
        var currentCharacterId =
            Plugin.PlayerState.IsLoaded
                ? Plugin.PlayerState.ContentId
                : 0;

        if (currentCharacterId == 0)
            return;

        StartGlobalPlannerTask(
            requirementSet,
            resolutionPolicy,
            currentCharacterId,
            currentCharacterId,
            replanMessage: null,
            autoStartExecution: false);
    }

    private void StartGlobalPlannerTask(
        RequirementSet requirementSet,
        ResolutionPolicy resolutionPolicy,
        ulong mainCharacterId,
        ulong currentCharacterId,
        string? replanMessage,
        bool autoStartExecution)
    {
        var started =
            globalPlannerCoordinator.TryStart(
                requirementSet,
                resolutionPolicy,
                mainCharacterId,
                currentCharacterId,
                plugin.InventoryIndex.Items,
                optimizationSettings,
                plugin.LastSyncAtUtc,
                replanMessage == null
                    ? resolverCoordinator.Snapshot.Plan?.Missing
                    : null,
                replanMessage,
                autoStartExecution);

        if (!started)
            return;

        ResetPlanExecutionSession();
    }

    private void DrawGlobalPlannerSearchDiagnostics(
        GlobalTransferPlannerDiagnosticsSnapshot diagnostics)
    {
        ImGui.Text("DIAGNOSTICA OPTIMIZER");
        ImGui.Separator();

        ImGui.Text(
            $"Nodi allocazione visitati: {diagnostics.SearchCalls:N0}");

        ImGui.Text(
            $"Piani candidati compilati: {diagnostics.UniqueStates:N0}");

        ImGui.Text(
            $"Piani candidati scartati: {diagnostics.DominatedStates:N0}");

        ImGui.Text(
            $"Candidati registrati: {diagnostics.MemoStates:N0}");

        ImGui.Text(
            $"Entry candidate: {diagnostics.MemoEntries:N0}");

        ImGui.Text(
            $"Azioni generate: {diagnostics.GeneratedActions:N0}");

        ImGui.Text(
            $"Azioni applicate: {diagnostics.AppliedActions:N0}");

        ImGui.Text(
            $"MOVE generati: {diagnostics.MoveActionsGenerated:N0}");

        ImGui.Text(
            $"SWITCH generati: {diagnostics.SwitchActionsGenerated:N0}");

        ImGui.Text(
            $"Profondità massima: {diagnostics.MaxDepth:N0}");

        ImGui.Text(
            $"Tempo: {diagnostics.ElapsedMilliseconds:N0} ms");

        ImGui.Text(
            $"Managed heap start (processo): {FormatMegabytes(diagnostics.ManagedMemoryStartBytes):N1} MB");

        ImGui.Text(
            $"Managed heap corrente (processo): {FormatMegabytes(diagnostics.ManagedMemoryBytes):N1} MB");

        ImGui.Text(
            $"Delta managed corrente: {FormatSignedMegabytes(diagnostics.ManagedMemoryBytes - diagnostics.ManagedMemoryStartBytes)}");

        ImGui.Text(
            $"Picco managed heap (processo): {FormatMegabytes(diagnostics.PeakManagedMemoryBytes):N1} MB");

        ImGui.Text(
            $"Delta picco managed: {FormatSignedMegabytes(diagnostics.PeakManagedMemoryBytes - diagnostics.ManagedMemoryStartBytes)}");

        if (diagnostics.ManagedMemoryEndBytes > 0)
        {
            ImGui.Text(
                $"Managed heap fine (processo): {FormatMegabytes(diagnostics.ManagedMemoryEndBytes):N1} MB");

            ImGui.Text(
                $"Delta managed finale: {FormatSignedMegabytes(diagnostics.ManagedMemoryEndBytes - diagnostics.ManagedMemoryStartBytes)}");
        }

        ImGui.Text(
            $"Allocato cumulativo dal thread planner: {FormatMegabytes(diagnostics.AllocatedBytes):N1} MB");

        if (diagnostics.TopItems.Count > 0)
        {
            ImGui.Spacing();
            ImGui.Text("TOP ITEM PER AZIONI GENERATE");

            foreach (var item in diagnostics.TopItems)
            {
                ImGui.Text(
                    $"{GetItemName(item.BaseItemId)} ({item.BaseItemId}) | {item.GeneratedActions:N0}");
            }
        }

        if (diagnostics.TopSources.Count > 0)
        {
            ImGui.Spacing();
            ImGui.Text("TOP SOURCE PER AZIONI GENERATE");

            foreach (var source in diagnostics.TopSources)
            {
                var inventorySource =
                    new InventorySource(
                        source.Storage,
                        source.OwnerId,
                        source.Container,
                        source.ParentCharacterId);

                ImGui.Text(
                    $"{GetSourceName(inventorySource)} | " +
                    $"{GetContainerName(inventorySource)} | " +
                    $"{source.GeneratedActions:N0}");
            }
        }
    }

    private string BuildGlobalPlannerDiagnosticsClipboardText(
        RequirementSet requirementSet,
        GlobalTransferPlannerDiagnosticsSnapshot diagnostics)
    {
        var lines =
            new List<string>
            {
                "FROG DEBUG | GLOBAL TRANSFER PLANNER | SEARCH DIAGNOSTICS",
                $"GeneratedUtc={DateTime.UtcNow:O}",
                $"MainCharacterId={(Plugin.PlayerState.IsLoaded ? Plugin.PlayerState.ContentId : 0)}",
                $"Requirements={requirementSet.Requirements.Count}",
                $"SearchCalls={diagnostics.SearchCalls}",
                $"UniqueStates={diagnostics.UniqueStates}",
                $"DominatedStates={diagnostics.DominatedStates}",
                $"MemoStates={diagnostics.MemoStates}",
                $"MemoEntries={diagnostics.MemoEntries}",
                $"GeneratedActions={diagnostics.GeneratedActions}",
                $"AppliedActions={diagnostics.AppliedActions}",
                $"MoveActionsGenerated={diagnostics.MoveActionsGenerated}",
                $"SwitchActionsGenerated={diagnostics.SwitchActionsGenerated}",
                $"MaxDepth={diagnostics.MaxDepth}",
                $"ElapsedMs={diagnostics.ElapsedMilliseconds}",
                $"ManagedMemoryStartBytes={diagnostics.ManagedMemoryStartBytes}",
                $"ManagedMemoryBytes={diagnostics.ManagedMemoryBytes}",
                $"ManagedMemoryDeltaBytes={diagnostics.ManagedMemoryBytes - diagnostics.ManagedMemoryStartBytes}",
                $"PeakManagedMemoryBytes={diagnostics.PeakManagedMemoryBytes}",
                $"PeakManagedMemoryDeltaBytes={diagnostics.PeakManagedMemoryBytes - diagnostics.ManagedMemoryStartBytes}",
                $"ManagedMemoryEndBytes={diagnostics.ManagedMemoryEndBytes}",
                $"ManagedMemoryEndDeltaBytes={(diagnostics.ManagedMemoryEndBytes > 0 ? diagnostics.ManagedMemoryEndBytes - diagnostics.ManagedMemoryStartBytes : 0)}",
                $"PlannerThreadAllocatedBytes={diagnostics.AllocatedBytes}"
            };

        if (diagnostics.TopItems.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("===== TOP ITEMS BY GENERATED ACTIONS =====");

            foreach (var item in diagnostics.TopItems)
            {
                lines.Add(
                    string.Join(
                        "\t",
                        GetItemName(item.BaseItemId),
                        item.BaseItemId,
                        item.GeneratedActions));
            }
        }

        if (diagnostics.TopSources.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("===== TOP SOURCES BY GENERATED ACTIONS =====");

            foreach (var source in diagnostics.TopSources)
            {
                var inventorySource =
                    new InventorySource(
                        source.Storage,
                        source.OwnerId,
                        source.Container,
                        source.ParentCharacterId);

                lines.Add(
                    string.Join(
                        "\t",
                        source.Storage,
                        GetSourceName(inventorySource),
                        source.OwnerId,
                        source.Container,
                        source.ParentCharacterId,
                        source.GeneratedActions));
            }
        }

        return string.Join(
            Environment.NewLine,
            lines);
    }

    private string BuildResolverRuntimeDiagnosticsClipboardText(
        RequirementSet requirementSet,
        ResolutionPolicy resolutionPolicy,
        ResolverCoordinatorSnapshot resolverState)
    {
        var lines =
            new List<string>
            {
                "FROG DEBUG | RESOLVER CACHE DIAGNOSTICS",
                $"GeneratedUtc={DateTime.UtcNow:O}",
                $"Requirements={requirementSet.Requirements.Count}",
                $"Sources={resolutionPolicy.Sources.Count}",
                $"ComputeCalls={resolverState.ComputeCalls}",
                $"LastElapsedMs={resolverState.LastElapsedMilliseconds:F6}",
                $"LastAllocatedBytes={resolverState.LastAllocatedBytes}",
                $"PeakAllocatedBytes={resolverState.PeakAllocatedBytes}",
                $"TotalAllocatedBytes={resolverState.TotalAllocatedBytes}"
            };

        return string.Join(
            Environment.NewLine,
            lines);
    }

    private static double FormatMegabytes(
        long bytes)
    {
        return bytes /
               (1024d * 1024d);
    }

    private static string FormatSignedMegabytes(
        long bytes)
    {
        var megabytes =
            FormatMegabytes(bytes);

        return $"{megabytes:+0.0;-0.0;0.0} MB";
    }

    private string BuildGlobalPlannerClipboardText(
        RequirementSet requirementSet,
        PlannerPlan plan)
    {
        var plannerState =
            globalPlannerCoordinator.Snapshot;

        var lines =
            new List<string>
            {
                "FROG DEBUG | GLOBAL TRANSFER PLANNER",
                $"GeneratedUtc={DateTime.UtcNow:O}",
                $"MainCharacterId={(Plugin.PlayerState.IsLoaded ? Plugin.PlayerState.ContentId : 0)}",
                $"Requirements={requirementSet.Requirements.Count}",
                $"Result={plan.Result}",
                $"Missing={plan.Missing}",
                $"ResolverMissingSnapshot={(plannerState.ResolverMissingSnapshot.HasValue ? plannerState.ResolverMissingSnapshot.Value : -1)}",
                $"MissingDeltaVsResolver={(plannerState.ResolverMissingSnapshot.HasValue ? plan.Missing - plannerState.ResolverMissingSnapshot.Value : 0)}",
                $"ResolverSyncSnapshotUtc={(plannerState.ResolverSyncSnapshot.HasValue ? plannerState.ResolverSyncSnapshot.Value.ToString("O") : "n/a")}",
                $"CharacterSwitches={plan.CharacterSwitches}",
                $"RetainerAccesses={plan.RetainerAccesses}",
                $"TransferHops={plan.TransferHops}",
                $"Actions={plan.Actions.Count}"
            };

        if (plannerState.Diagnostics != null)
        {
            var diagnostics =
                plannerState.Diagnostics.Snapshot();

            lines.Add(string.Empty);
            lines.Add("===== SEARCH DIAGNOSTICS =====");
            lines.Add($"SearchCalls={diagnostics.SearchCalls}");
            lines.Add($"UniqueStates={diagnostics.UniqueStates}");
            lines.Add($"DominatedStates={diagnostics.DominatedStates}");
            lines.Add($"MemoStates={diagnostics.MemoStates}");
            lines.Add($"MemoEntries={diagnostics.MemoEntries}");
            lines.Add($"GeneratedActions={diagnostics.GeneratedActions}");
            lines.Add($"AppliedActions={diagnostics.AppliedActions}");
            lines.Add($"MoveActionsGenerated={diagnostics.MoveActionsGenerated}");
            lines.Add($"SwitchActionsGenerated={diagnostics.SwitchActionsGenerated}");
            lines.Add($"MaxDepth={diagnostics.MaxDepth}");
            lines.Add($"ElapsedMs={diagnostics.ElapsedMilliseconds}");
            lines.Add($"ManagedMemoryStartBytes={diagnostics.ManagedMemoryStartBytes}");
            lines.Add($"ManagedMemoryBytes={diagnostics.ManagedMemoryBytes}");
            lines.Add($"ManagedMemoryDeltaBytes={diagnostics.ManagedMemoryBytes - diagnostics.ManagedMemoryStartBytes}");
            lines.Add($"PeakManagedMemoryBytes={diagnostics.PeakManagedMemoryBytes}");
            lines.Add($"PeakManagedMemoryDeltaBytes={diagnostics.PeakManagedMemoryBytes - diagnostics.ManagedMemoryStartBytes}");
            lines.Add($"ManagedMemoryEndBytes={diagnostics.ManagedMemoryEndBytes}");
            lines.Add($"ManagedMemoryEndDeltaBytes={(diagnostics.ManagedMemoryEndBytes > 0 ? diagnostics.ManagedMemoryEndBytes - diagnostics.ManagedMemoryStartBytes : 0)}");
            lines.Add($"PlannerThreadAllocatedBytes={diagnostics.AllocatedBytes}");
        }

        lines.Add(string.Empty);
        lines.Add("===== ACTIONS =====");

        for (var actionIndex = 0;
             actionIndex < plan.Actions.Count;
             actionIndex++)
        {
            var action =
                plan.Actions[actionIndex];

            var actionNumber =
                actionIndex + 1;
            if (action.Type == PlannerActionType.SwitchCharacter)
            {
                lines.Add(
                    string.Join(
                        "\t",
                        actionNumber,
                        "SWITCH",
                        GetCharacterName(action.FromCharacterId),
                        action.FromCharacterId,
                        GetCharacterName(action.ToCharacterId),
                        action.ToCharacterId));

                continue;
            }

            if (action.Source is null ||
                action.Destination is null)
            {
                continue;
            }

            lines.Add(
                string.Join(
                    "\t",
                    actionNumber,
                    "MOVE",
                    GetItemName(action.BaseItemId),
                    action.BaseItemId,
                    action.IsHq ? "HQ" : "NQ",
                    action.Quantity,
                    GetSourceName(action.Source),
                    action.Source.Storage,
                    action.Source.OwnerId,
                    action.Source.ParentCharacterId,
                    GetActionSourceLocationText(
                        plan,
                        actionIndex,
                        action),
                    GetSourceName(action.Destination),
                    action.Destination.Storage,
                    action.Destination.OwnerId,
                    action.Destination.ParentCharacterId,
                    GetContainerName(action.Destination)));

        }

        return string.Join(
            Environment.NewLine,
            lines);
    }

    private string GetCharacterName(
        ulong characterId)
    {
        if (characterId == 0)
            return "Unknown";

        var catalogName =
            characterCatalog.GetName(characterId);

        if (!string.IsNullOrWhiteSpace(catalogName))
            return catalogName;

        if (Plugin.PlayerState.IsLoaded &&
            Plugin.PlayerState.ContentId == characterId)
        {
            return Plugin.PlayerState.CharacterName;
        }

        var character =
            characterMonitor.GetCharacterById(characterId);

        if (character != null &&
            !string.IsNullOrWhiteSpace(character.FormattedName))
        {
            return character.FormattedName;
        }

        var name =
            characterMonitor.GetCharacterNameById(characterId);

        if (!string.IsNullOrWhiteSpace(name))
            return name;

        return $"Character {characterId}";
    }

    private void DrawClientDiagnostics()
    {
        ImGui.Text("CLIENT");
        ImGui.Separator();

        var territoryId = Plugin.ClientState.TerritoryType;

        ImGui.Text($"Territory ID: {territoryId}");

        if (Plugin.DataManager
            .GetExcelSheet<TerritoryType>()
            .TryGetRow(territoryId, out var territoryRow))
        {
            ImGui.Text(
                $"Zona: {territoryRow.PlaceName.Value.Name}");
        }
    }

    private void DrawInventoryDiagnostics()
    {
        DrawInventoryContainer(
            "Inventory 1",
            GameInventoryType.Inventory1);

        DrawInventoryContainer(
            "Inventory 2",
            GameInventoryType.Inventory2);

        DrawInventoryContainer(
            "Inventory 3",
            GameInventoryType.Inventory3);

        DrawInventoryContainer(
            "Inventory 4",
            GameInventoryType.Inventory4);
    }

    private void DrawIndexSearch()
    {
        ImGui.InputInt(
            "Base Item ID",
            ref searchItemId);

        if (searchItemId <= 0)
            return;

        var baseItemId = (uint)searchItemId;

        var totalQuantity =
            plugin.InventoryIndex.GetTotalQuantity(baseItemId);

        var nqQuantity =
            plugin.InventoryIndex.GetNqQuantity(baseItemId);

        var hqQuantity =
            plugin.InventoryIndex.GetHqQuantity(baseItemId);

        ImGui.Text(
            $"Item: {GetItemName(baseItemId)}");

        ImGui.Text(
            $"Base Item ID: {baseItemId}");

        ImGui.Text(
            $"Quantità totale: {totalQuantity}");

        ImGui.Text(
            $"NQ: {nqQuantity}");

        ImGui.Text(
            $"HQ: {hqQuantity}");
    }

    private void DrawStorageIndexDiagnostics()
    {
        ImGui.Text(
            $"Character Inventory: " +
            $"{plugin.InventoryIndex.Items.Count(x => x.Storage == StorageType.CharacterInventory)} snapshot");

        ImGui.Text(
            $"Retainer: " +
            $"{plugin.InventoryIndex.Items.Count(x => x.Storage == StorageType.Retainer)} snapshot");

        ImGui.Text(
            $"Free Company Chest: " +
            $"{plugin.InventoryIndex.Items.Count(x => x.Storage == StorageType.FreeCompanyChest)} snapshot");

        ImGui.Text(
            $"Totale Index: {plugin.InventoryIndex.Items.Count} snapshot");

        var retainerOwners =
            plugin.InventoryIndex.Items
                .Where(x => x.Storage == StorageType.Retainer)
                .Select(x => x.OwnerId)
                .Distinct()
                .Count();

        if (retainerOwners > 0)
        {
            ImGui.Text(
                $"Retainer presenti nell'Index: {retainerOwners}");
        }

        var freeCompanies =
            plugin.InventoryIndex.Items
                .Where(x => x.Storage == StorageType.FreeCompanyChest)
                .Select(x => x.OwnerId)
                .Distinct()
                .Count();

        if (freeCompanies > 0)
        {
            ImGui.Text(
                $"Free Company presenti nell'Index: {freeCompanies}");
        }

    }

    private void DrawSyncDiagnostics()
    {
        ImGui.Text("DEBUG SYNC");
        ImGui.Separator();

        if (plugin.LastSyncAtUtc == null)
        {
            ImGui.Text(
                "Nessuna sincronizzazione eseguita.");

            return;
        }

        ImGui.Text(
            $"Ultimo sync UTC: {plugin.LastSyncAtUtc:HH:mm:ss.fff}");

        ImGui.Text(
            $"Character ID: {plugin.LastSyncCharacterId}");

        ImGui.Spacing();

        if (searchItemId <= 0)
        {
            ImGui.TextWrapped(
                "Inserisci il Base Item ID nella sezione \"RICERCA NELL'INDICE\" per visualizzare la tabella di debug.");

            return;
        }

        var baseItemId = (uint)searchItemId;

        ImGui.Text(
            $"DEBUG ITEM: {GetItemName(baseItemId)}");

        ImGui.Text(
            $"Base Item ID: {baseItemId}");

        ImGui.Spacing();

        if (ImGui.Button("COPIA DEBUG"))
        {
            var clipboardText =
                BuildDebugClipboardText(baseItemId);

            ImGui.SetClipboardText(clipboardText);
        }

        ImGui.SameLine();

        ImGui.Text(
            "Copia GAME INVENTORY + INDEX");

        ImGui.Spacing();

        var liveSnapshots =
            plugin.LastSyncSnapshots
                .Where(x =>
                    x.BaseItemId == baseItemId)
                .OrderBy(x => x.Container)
                .ThenBy(x => x.Slot)
                .ToList();

        var indexSnapshots =
            plugin.InventoryIndex.Items
                .Where(x =>
                    x.BaseItemId == baseItemId)
                .OrderBy(x => x.Storage)
                .ThenBy(x => x.OwnerId)
                .ThenBy(x => x.Container)
                .ThenBy(x => x.Slot)
                .ToList();

        ImGui.Text(
            $"GAME INVENTORY: {liveSnapshots.Count} snapshot");

        ImGui.SameLine();

        ImGui.Text(
            $" | INDEX: {indexSnapshots.Count} snapshot");

        ImGui.Spacing();

        DrawDebugTable(
            liveSnapshots,
            indexSnapshots);

        ImGui.Spacing();
        ImGui.Spacing();
    }

    private void DrawDebugTable(
        IReadOnlyList<InventoryItemSnapshot> liveSnapshots,
        IReadOnlyList<InventoryItemSnapshot> indexSnapshots)
    {
        if (!ImGui.BeginTable(
                "FrogDebugTable",
                11,
                ImGuiTableFlags.Borders |
                ImGuiTableFlags.RowBg |
                ImGuiTableFlags.ScrollX |
                ImGuiTableFlags.ScrollY |
                ImGuiTableFlags.Resizable |
                ImGuiTableFlags.SizingFixedFit,
                new Vector2(0, 350)))
        {
            return;
        }

        ImGui.TableSetupColumn("Fonte");
        ImGui.TableSetupColumn("Storage");
        ImGui.TableSetupColumn("Owner");
        ImGui.TableSetupColumn("Owner ID");
        ImGui.TableSetupColumn("Container");
        ImGui.TableSetupColumn("Slot");
        ImGui.TableSetupColumn("Raw ID");
        ImGui.TableSetupColumn("Base ID");
        ImGui.TableSetupColumn("Qty");
        ImGui.TableSetupColumn("HQ");
        ImGui.TableSetupColumn("Verified");

        ImGui.TableHeadersRow();

        foreach (var snapshot in liveSnapshots)
        {
            DrawDebugRow(
                "GAME INVENTORY",
                snapshot);
        }

        foreach (var snapshot in indexSnapshots)
        {
            DrawDebugRow(
                "INDEX",
                snapshot);
        }

        ImGui.EndTable();
    }

    private void DrawDebugRow(
        string sourceName,
        InventoryItemSnapshot snapshot)
    {
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(sourceName);

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(snapshot.Storage.ToString());

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(GetOwnerName(snapshot));

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(snapshot.OwnerId.ToString());

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(GetContainerName(snapshot));

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(snapshot.Slot.ToString());

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(snapshot.RawItemId.ToString());

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(snapshot.BaseItemId.ToString());

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(snapshot.Quantity.ToString());

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(
            snapshot.IsHq ? "HQ" : "NQ");

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(
            snapshot.IsVerified ? "YES" : "NO");
    }

    private string BuildDebugClipboardText(
        uint baseItemId)
    {
        var liveSnapshots =
            plugin.LastSyncSnapshots
                .Where(x =>
                    x.BaseItemId == baseItemId)
                .OrderBy(x => x.Container)
                .ThenBy(x => x.Slot)
                .ToList();

        var indexSnapshots =
            plugin.InventoryIndex.Items
                .Where(x =>
                    x.BaseItemId == baseItemId)
                .OrderBy(x => x.Storage)
                .ThenBy(x => x.OwnerId)
                .ThenBy(x => x.Container)
                .ThenBy(x => x.Slot)
                .ToList();

        var lines =
            new List<string>
            {
                $"FROG DEBUG | Item={GetItemName(baseItemId)} | BaseItemId={baseItemId}",
                $"GeneratedUtc={DateTime.UtcNow:O}",
                $"LastSyncUtc={plugin.LastSyncAtUtc:O}",
                $"CharacterId={plugin.LastSyncCharacterId}",
                string.Empty,
                "SOURCE\tSTORAGE\tOWNER\tOWNER_ID\tCONTAINER\tSLOT\tRAW_ID\tBASE_ID\tQTY\tHQ\tVERIFIED"
            };

        foreach (var snapshot in liveSnapshots)
        {
            lines.Add(
                BuildDebugClipboardRow(
                    "GAME INVENTORY",
                    snapshot));
        }

        foreach (var snapshot in indexSnapshots)
        {
            lines.Add(
                BuildDebugClipboardRow(
                    "INDEX",
                    snapshot));
        }

        return string.Join(
            Environment.NewLine,
            lines);
    }

    private string BuildDebugClipboardRow(
        string sourceName,
        InventoryItemSnapshot snapshot)
    {
        return string.Join(
            "\t",
            sourceName,
            snapshot.Storage,
            GetOwnerName(snapshot),
            snapshot.OwnerId,
            GetContainerName(snapshot),
            snapshot.Slot,
            snapshot.RawItemId,
            snapshot.BaseItemId,
            snapshot.Quantity,
            snapshot.IsHq ? "HQ" : "NQ",
            snapshot.IsVerified ? "YES" : "NO");
    }

    private string BuildResolverClipboardText(
        RequirementSet requirementSet,
        InventorySourceCatalog sourceCatalog,
        ResolutionPolicy resolutionPolicy,
        TransferPlan plan)
    {
        var lines =
            new List<string>
            {
                "FROG DEBUG | RESOLVER / TRANSFER PLAN",
                $"GeneratedUtc={DateTime.UtcNow:O}",
                $"MainCharacterId={(Plugin.PlayerState.IsLoaded ? Plugin.PlayerState.ContentId : 0)}",
                $"Requirements={requirementSet.Requirements.Count}",
                $"Sources={resolutionPolicy.Sources.Count}",
                $"Resolutions={plan.Resolutions.Count}",
                $"Intents={plan.Intents.Count}",
                $"Missing={plan.Missing}",
                $"Complete={plan.IsComplete}",
                string.Empty,
                "===== ORDINE PRIORITÀ =====",
                BuildPriorityClipboardText(
                    resolutionPolicy,
                    sourceCatalog),
                string.Empty,
                "===== RESOLUTION =====",
                BuildResolutionClipboardText(
                    requirementSet,
                    plan),
                string.Empty,
                "===== TRANSFER INTENTS =====",
                BuildTransferIntentsClipboardText(plan)
            };

        return string.Join(
            Environment.NewLine,
            lines);
    }

    private string BuildPriorityClipboardText(
        ResolutionPolicy resolutionPolicy,
        InventorySourceCatalog sourceCatalog)
    {
        var lines =
            new List<string>
            {
                "FROG DEBUG | ORDINE PRIORITÀ",
                $"GeneratedUtc={DateTime.UtcNow:O}",
                $"MainCharacterId={(Plugin.PlayerState.IsLoaded ? Plugin.PlayerState.ContentId : 0)}",
                string.Empty,
                "PRIORITY\tSTORAGE\tSOURCE\tOWNER_ID\tCONTAINER\tREAD\tUSE"
            };

        for (var i = 0;
             i < resolutionPolicy.Sources.Count;
             i++)
        {
            var source =
                resolutionPolicy.Sources[i];

            var sourcePolicy =
                sourceCatalog.Sources
                    .FirstOrDefault(x => x.Source == source);

            lines.Add(
                string.Join(
                    "\t",
                    i + 1,
                    source.Storage,
                    GetSourceName(source),
                    source.OwnerId,
                    source.Container,
                    sourcePolicy?.Read == true ? "YES" : "NO",
                    sourcePolicy?.Use == true ? "YES" : "NO"));
        }

        return string.Join(
            Environment.NewLine,
            lines);
    }

    private string BuildResolutionClipboardText(
        RequirementSet requirementSet,
        TransferPlan plan)
    {
        var lines =
            new List<string>
            {
                "FROG DEBUG | RESOLUTION",
                $"GeneratedUtc={DateTime.UtcNow:O}",
                $"Complete={plan.IsComplete}",
                $"Missing={plan.Missing}",
                string.Empty,
                "ITEM\tREQUIRED\tAVAILABLE\tMISSING\tSOURCE\tQUALITY\tQTY"
            };

        foreach (var resolution in plan.Resolutions)
        {
            if (resolution.Allocations.Count == 0)
            {
                lines.Add(
                    string.Join(
                        "\t",
                        GetItemName(resolution.Requirement.BaseItemId),
                        resolution.Requirement.Quantity,
                        resolution.Available,
                        resolution.Missing,
                        "-",
                        "-",
                        0));

                continue;
            }

            foreach (var allocation in resolution.Allocations)
            {
                lines.Add(
                    string.Join(
                        "\t",
                        GetItemName(resolution.Requirement.BaseItemId),
                        resolution.Requirement.Quantity,
                        resolution.Available,
                        resolution.Missing,
                        GetSourceName(allocation.Source),
                        allocation.IsHq ? "HQ" : "NQ",
                        allocation.Quantity));
            }
        }

        return string.Join(
            Environment.NewLine,
            lines);
    }

    private string BuildTransferIntentsClipboardText(
        TransferPlan plan)
    {
        var lines =
            new List<string>
            {
                "FROG DEBUG | TRANSFER INTENTS",
                $"GeneratedUtc={DateTime.UtcNow:O}",
                $"IntentCount={plan.Intents.Count}",
                $"Complete={plan.IsComplete}",
                $"Missing={plan.Missing}",
                string.Empty,
                "ITEM\tBASE_ID\tSOURCE\tSTORAGE\tOWNER_ID\tCONTAINER\tQUALITY\tQTY"
            };

        foreach (var intent in plan.Intents)
        {
            lines.Add(
                string.Join(
                    "\t",
                    GetItemName(intent.BaseItemId),
                    intent.BaseItemId,
                    GetSourceName(intent.Source),
                    intent.Source.Storage,
                    intent.Source.OwnerId,
                    intent.Source.Container,
                    intent.IsHq ? "HQ" : "NQ",
                    intent.Quantity));
        }

        return string.Join(
            Environment.NewLine,
            lines);
    }

    private static void DrawBuildInfo()
    {
        var assemblyPath =
            Plugin.PluginInterface.AssemblyLocation.FullName;

        if (!string.IsNullOrWhiteSpace(assemblyPath) &&
            File.Exists(assemblyPath))
        {
            var lastWriteTime =
                File.GetLastWriteTime(assemblyPath);

            ImGui.Text(
                $"DLL aggiornata: {lastWriteTime:dd/MM/yyyy HH:mm:ss}");

            ImGui.Text(
                $"DLL: {assemblyPath}");
        }
        else
        {
            ImGui.Text(
                "DLL aggiornata: percorso non disponibile.");
        }

        var version =
            typeof(Plugin).Assembly.GetName().Version;

        if (version != null)
        {
            ImGui.Text(
                $"Versione assembly: {version}");
        }
    }

    private static void DrawInventoryContainer(
        string containerName,
        GameInventoryType inventoryType)
    {
        var inventoryItems =
            Plugin.GameInventory.GetInventoryItems(inventoryType);

        ImGui.Text(
            $"{containerName} - Slot letti: {inventoryItems.Length}");

        var shown = 0;

        foreach (var item in inventoryItems)
        {
            if (item.ItemId == 0)
                continue;

            ImGui.Text(
                $"{GetItemName(item.BaseItemId)} | " +
                $"Base {item.BaseItemId} | " +
                $"Raw {item.ItemId} x{item.Quantity}" +
                (item.IsHq ? " [HQ]" : " [NQ]"));

            shown++;
        }

        if (shown == 0)
        {
            ImGui.Text("Vuoto.");
        }

        ImGui.Spacing();
    }

    private static string GetItemName(
        uint baseItemId)
    {
        if (baseItemId == 0)
            return "Unknown Item";

        var sheet =
            Plugin.DataManager.GetExcelSheet<Item>();

        if (sheet.TryGetRow(
                baseItemId,
                out var item))
        {
            var name = item.Name.ToString();

            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }

        return $"Unknown Item ({baseItemId})";
    }

    private string GetSourceName(
        InventorySource source)
    {
        var ownerName =
            source.OwnerName;

        if (string.IsNullOrWhiteSpace(ownerName))
        {
            ownerName =
                source.Storage switch
                {
                    StorageType.CharacterInventory =>
                        GetCharacterInventorySourceName(source),

                    StorageType.Retainer =>
                        GetRetainerSourceName(source),

                    StorageType.FreeCompanyChest =>
                        GetFreeCompanySourceName(source),

                    _ =>
                        source.Storage.ToString()
                };
        }

        if (source.Storage == StorageType.Retainer)
        {
            var parentName =
                GetCharacterName(source.ParentCharacterId);

            if (!string.IsNullOrWhiteSpace(parentName) &&
                !parentName.StartsWith(
                    "Character ",
                    StringComparison.Ordinal))
            {
                return $"{ownerName} ({parentName})";
            }
        }

        return ownerName;
    }

    private string GetCharacterInventorySourceName(
        InventorySource source)
    {
        return GetCharacterName(source.OwnerId);
    }

    private string GetRetainerSourceName(
        InventorySource source)
    {
        var catalogName =
            characterCatalog.GetName(
                source.OwnerId);

        if (!string.IsNullOrWhiteSpace(catalogName))
            return catalogName;

        var retainerName =
            characterMonitor.GetCharacterNameById(
                source.OwnerId);

        if (!string.IsNullOrWhiteSpace(retainerName))
            return retainerName;

        var retainer =
            characterMonitor.GetCharacterById(
                source.OwnerId);

        if (retainer != null &&
            !string.IsNullOrWhiteSpace(
                retainer.FormattedName))
        {
            return retainer.FormattedName;
        }

        return $"Retainer {source.OwnerId}";
    }

    private string GetFreeCompanySourceName(
        InventorySource source)
    {
        var catalogName =
            characterCatalog.GetName(
                source.OwnerId);

        if (!string.IsNullOrWhiteSpace(catalogName))
            return catalogName;

        var freeCompanyName =
            characterMonitor.GetCharacterNameById(
                source.OwnerId);

        if (!string.IsNullOrWhiteSpace(
                freeCompanyName))
        {
            return freeCompanyName;
        }

        var freeCompany =
            characterMonitor.GetCharacterById(
                source.OwnerId);

        if (freeCompany != null &&
            !string.IsNullOrWhiteSpace(
                freeCompany.FormattedName))
        {
            return freeCompany.FormattedName;
        }

        var name =
            freeCompany?.Name.ToString();

        if (!string.IsNullOrWhiteSpace(name))
            return name;

        return $"Free Company {source.OwnerId}";
    }

    private string GetOwnerName(
        InventoryItemSnapshot snapshot)
    {
        return snapshot.Storage switch
        {
            StorageType.CharacterInventory =>
                GetCharacterInventoryOwnerName(snapshot.OwnerId),

            StorageType.Retainer =>
                GetRetainerOwnerName(snapshot.OwnerId),

            StorageType.FreeCompanyChest =>
                GetFreeCompanyOwnerName(snapshot.OwnerId),

            _ =>
                snapshot.OwnerId.ToString()
        };
    }

    private string GetCharacterInventoryOwnerName(
        ulong ownerId)
    {
        return GetCharacterName(ownerId);
    }

    private string GetRetainerOwnerName(
        ulong ownerId)
    {
        var catalogName =
            characterCatalog.GetName(ownerId);

        if (!string.IsNullOrWhiteSpace(catalogName))
            return catalogName;

        var retainerName =
            characterMonitor.GetCharacterNameById(
                ownerId);

        if (!string.IsNullOrWhiteSpace(retainerName))
            return retainerName;

        var retainer =
            characterMonitor.GetCharacterById(
                ownerId);

        if (retainer != null &&
            !string.IsNullOrWhiteSpace(
                retainer.FormattedName))
        {
            return retainer.FormattedName;
        }

        return $"Retainer {ownerId}";
    }

    private string GetFreeCompanyOwnerName(
        ulong ownerId)
    {
        var catalogName =
            characterCatalog.GetName(ownerId);

        if (!string.IsNullOrWhiteSpace(catalogName))
            return catalogName;

        var freeCompanyName =
            characterMonitor.GetCharacterNameById(
                ownerId);

        if (!string.IsNullOrWhiteSpace(
                freeCompanyName))
        {
            return freeCompanyName;
        }

        var freeCompany =
            characterMonitor.GetCharacterById(
                ownerId);

        var name =
            freeCompany?.Name.ToString();

        if (!string.IsNullOrWhiteSpace(name))
            return name;

        return $"Free Company {ownerId}";
    }

    private string GetActionSourceLocationText(
        PlannerPlan plan,
        int actionIndex,
        PlannerAction action)
    {
        if (action.Source is null)
            return "Source non disponibile";

        if (action.Source.Storage != StorageType.Retainer)
        {
            return GetContainerName(
                action.Source);
        }

        var displayLocation =
            retainerDisplayLocator.LocateSource(
                plan,
                actionIndex);

        var internalContainer =
            GetContainerName(
                action.Source);

        if (!displayLocation.IsAvailable)
        {
            return
                $"Posizione visibile retainer non disponibile | {internalContainer}";
        }

        var visiblePositions =
            string.Join(
                ", ",
                displayLocation.Positions
                    .Select(position =>
                        $"Pagina visibile {position.Page}, slot {position.Slot} x{position.Quantity}"));

        return
            $"{visiblePositions} | {internalContainer}";
    }

    private static string GetContainerName(
        InventoryItemSnapshot snapshot)
    {
        return GetContainerName(
            new InventorySource(
                snapshot.Storage,
                snapshot.OwnerId,
                snapshot.Container));
    }

    private static string GetContainerName(
        InventorySource source)
    {
        return source.Storage switch
        {
            StorageType.CharacterInventory =>
                GetCharacterContainerName(
                    source.Container),

            StorageType.Retainer =>
                GetRetainerContainerName(
                    source.Container),

            StorageType.FreeCompanyChest =>
                GetFreeCompanyContainerName(
                    source.Container),

            _ =>
                $"Container {source.Container}"
        };
    }

    private static string GetCharacterContainerName(
        uint container)
    {
        return container switch
        {
            (uint)GameInventoryType.Inventory1 =>
                "Inventory 1",

            (uint)GameInventoryType.Inventory2 =>
                "Inventory 2",

            (uint)GameInventoryType.Inventory3 =>
                "Inventory 3",

            (uint)GameInventoryType.Inventory4 =>
                "Inventory 4",

            _ =>
                $"Inventory ({container})"
        };
    }

    private static string GetRetainerContainerName(
        uint container)
    {
        return container switch
        {
            10000 => "Retainer Internal Container 1",
            10001 => "Retainer Internal Container 2",
            10002 => "Retainer Internal Container 3",
            10003 => "Retainer Internal Container 4",
            10004 => "Retainer Internal Container 5",
            10005 => "Retainer Internal Container 6",
            10006 => "Retainer Internal Container 7",

            _ =>
                $"Retainer Container ({container})"
        };
    }

    private static string GetFreeCompanyContainerName(
        uint container)
    {
        return container switch
        {
            20000 => "FC Chest Page 1",
            20001 => "FC Chest Page 2",
            20002 => "FC Chest Page 3",
            20003 => "FC Chest Page 4",
            20004 => "FC Chest Page 5",

            _ =>
                $"FC Chest Container ({container})"
        };
    }
}
