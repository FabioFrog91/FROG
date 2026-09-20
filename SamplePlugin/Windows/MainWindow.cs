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
using System.Threading.Tasks;

namespace FROG.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly ICharacterMonitor characterMonitor;
    private readonly CharacterCatalog characterCatalog;

    private RequirementSet? importedRequirementSet;
    private PlannerPlan? globalPlannerPlan;
    private Task<PlannerPlan>? globalPlannerTask;
    private GlobalTransferPlannerDiagnostics? globalPlannerDiagnostics;
    private string? globalPlannerError;
    private int? globalPlannerResolverMissingSnapshot;
    private DateTime? globalPlannerResolverSyncSnapshot;
    private readonly OptimizationSettings optimizationSettings = new();

    private RequirementSet? resolverCachedRequirementSet;
    private IReadOnlyList<InventorySource>? resolverCachedSources;
    private InventorySourceCatalog? resolverCachedSourceCatalog;
    private ResolutionPolicy? resolverCachedResolutionPolicy;
    private TransferPlan? resolverCachedPlan;
    private DateTime? resolverCachedSyncAtUtc;
    private ulong resolverCachedCharacterId;

    private long resolverComputeCalls;
    private double resolverLastElapsedMilliseconds;
    private long resolverLastAllocatedBytes;
    private long resolverTotalAllocatedBytes;
    private long resolverPeakAllocatedBytes;

    private int searchItemId;

    public MainWindow(
        Plugin plugin,
        ICharacterMonitor characterMonitor,
        CharacterCatalog characterCatalog)
        : base("FROG")
    {
        this.plugin = plugin;
        this.characterMonitor = characterMonitor;
        this.characterCatalog = characterCatalog;

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

            InvalidateResolverCache(
                resetDiagnostics: true);

            globalPlannerPlan = null;
            globalPlannerDiagnostics = null;
            globalPlannerError = null;
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

        var indexItems =
            plugin.InventoryIndex.Items;

        if (indexItems.Count == 0)
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

        EnsureResolverCache(
            importedRequirementSet,
            indexItems,
            currentCharacterId);

        var sources =
            resolverCachedSources;

        var sourceCatalog =
            resolverCachedSourceCatalog;

        var resolutionPolicy =
            resolverCachedResolutionPolicy;

        var plan =
            resolverCachedPlan;

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
            $"Chiamate compute reali: {resolverComputeCalls:N0}");

        ImGui.Text(
            $"Ultimo compute: {resolverLastElapsedMilliseconds:N3} ms");

        ImGui.Text(
            $"Allocato ultimo compute: {FormatMegabytes(resolverLastAllocatedBytes):N3} MB");

        ImGui.Text(
            $"Picco allocato per compute: {FormatMegabytes(resolverPeakAllocatedBytes):N3} MB");

        ImGui.Text(
            $"Allocato cumulativo resolver: {FormatMegabytes(resolverTotalAllocatedBytes):N1} MB");

        ImGui.Spacing();

        if (ImGui.Button("COPIA DIAGNOSTICA RESOLVER"))
        {
            ImGui.SetClipboardText(
                BuildResolverRuntimeDiagnosticsClipboardText(
                    importedRequirementSet,
                    resolutionPolicy));
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

    private void EnsureResolverCache(
        RequirementSet requirementSet,
        IReadOnlyList<InventoryItemSnapshot> indexItems,
        ulong currentCharacterId)
    {
        var currentSyncAtUtc =
            plugin.LastSyncAtUtc;

        var cacheIsValid =
            resolverCachedPlan != null &&
            resolverCachedSources != null &&
            resolverCachedSourceCatalog != null &&
            resolverCachedResolutionPolicy != null &&
            ReferenceEquals(
                resolverCachedRequirementSet,
                requirementSet) &&
            resolverCachedSyncAtUtc == currentSyncAtUtc &&
            resolverCachedCharacterId == currentCharacterId;

        if (cacheIsValid)
            return;

        var resolverAllocatedBefore =
            GC.GetAllocatedBytesForCurrentThread();

        var resolverStopwatch =
            Stopwatch.StartNew();

        var sources =
            BuildPlannerSources(
                indexItems,
                currentCharacterId);

        if (sources.Count == 0)
        {
            InvalidateResolverCache(
                resetDiagnostics: false);

            return;
        }

        var sourceCatalog =
            new InventorySourceCatalog();

        foreach (var source in sources)
        {
            sourceCatalog.Add(
                new SourcePolicy(
                    source,
                    Read: true,
                    Use: true));
        }

        var resolutionPolicy =
            new ResolutionPolicy(
                sources,
                currentCharacterId);

        var planner =
            new TransferPlanner(
                new RequirementResolver());

        var plan =
            planner.Plan(
                requirementSet,
                plugin.InventoryIndex,
                sourceCatalog,
                resolutionPolicy);

        resolverStopwatch.Stop();

        resolverCachedRequirementSet =
            requirementSet;

        resolverCachedSources =
            sources;

        resolverCachedSourceCatalog =
            sourceCatalog;

        resolverCachedResolutionPolicy =
            resolutionPolicy;

        resolverCachedPlan =
            plan;

        resolverCachedSyncAtUtc =
            currentSyncAtUtc;

        resolverCachedCharacterId =
            currentCharacterId;

        resolverComputeCalls++;

        resolverLastElapsedMilliseconds =
            resolverStopwatch.Elapsed.TotalMilliseconds;

        resolverLastAllocatedBytes =
            Math.Max(
                0,
                GC.GetAllocatedBytesForCurrentThread() -
                resolverAllocatedBefore);

        resolverTotalAllocatedBytes +=
            resolverLastAllocatedBytes;

        resolverPeakAllocatedBytes =
            Math.Max(
                resolverPeakAllocatedBytes,
                resolverLastAllocatedBytes);
    }

    private void InvalidateResolverCache(
        bool resetDiagnostics)
    {
        resolverCachedRequirementSet = null;
        resolverCachedSources = null;
        resolverCachedSourceCatalog = null;
        resolverCachedResolutionPolicy = null;
        resolverCachedPlan = null;
        resolverCachedSyncAtUtc = null;
        resolverCachedCharacterId = 0;

        if (!resetDiagnostics)
            return;

        resolverComputeCalls = 0;
        resolverLastElapsedMilliseconds = 0;
        resolverLastAllocatedBytes = 0;
        resolverTotalAllocatedBytes = 0;
        resolverPeakAllocatedBytes = 0;
    }

    private void DrawGlobalPlannerDiagnostics(
        RequirementSet requirementSet,
        ResolutionPolicy resolutionPolicy)
    {
        TryCompleteGlobalPlannerTask();

        ImGui.Text("GLOBAL TRANSFER PLANNER");
        ImGui.Separator();

        ImGui.TextWrapped(
            "Planner globale: risolve l'intera lista considerando inventario principale, retainer, FC e cambi personaggio.");

        if (globalPlannerTask == null)
        {
            if (ImGui.Button("CALCOLA PIANO GLOBALE"))
            {
                StartGlobalPlannerTask(
                    requirementSet,
                    resolutionPolicy);
            }
        }
        else
        {
            ImGui.Text("Calcolo...");

            if (globalPlannerDiagnostics != null)
            {
                var diagnostics =
                    globalPlannerDiagnostics.Snapshot();

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

        if (!string.IsNullOrWhiteSpace(globalPlannerError))
        {
            ImGui.TextWrapped(
                $"Errore planner: {globalPlannerError}");

            return;
        }

        if (globalPlannerPlan == null)
        {
            if (globalPlannerTask == null)
            {
                ImGui.Text(
                    "Nessun piano globale calcolato.");
            }

            return;
        }

        ImGui.Text(
            $"Risultato: {globalPlannerPlan.Result}");

        ImGui.Text(
            $"Mancante: {globalPlannerPlan.Missing}");

        if (globalPlannerResolverMissingSnapshot.HasValue)
        {
            var delta =
                globalPlannerPlan.Missing -
                globalPlannerResolverMissingSnapshot.Value;

            ImGui.Text(
                $"Resolver stesso snapshot: {globalPlannerResolverMissingSnapshot.Value}");

            ImGui.Text(
                $"Delta planner-resolver: {delta:+#;-#;0}");
        }

        ImGui.Text(
            $"Cambi personaggio: {globalPlannerPlan.CharacterSwitches}");

        ImGui.Text(
            $"Accessi retainer: {globalPlannerPlan.RetainerAccesses}");

        ImGui.Text(
            $"Hop logici: {globalPlannerPlan.TransferHops}");

        ImGui.Text(
            $"Azioni: {globalPlannerPlan.Actions.Count}");

        if (globalPlannerDiagnostics != null)
        {
            ImGui.Spacing();

            DrawGlobalPlannerSearchDiagnostics(
                globalPlannerDiagnostics.Snapshot());
        }

        ImGui.Spacing();

        if (ImGui.Button("COPIA PIANO GLOBALE"))
        {
            ImGui.SetClipboardText(
                BuildGlobalPlannerClipboardText(
                    requirementSet,
                    globalPlannerPlan));
        }

        ImGui.Spacing();

        foreach (var action in globalPlannerPlan.Actions)
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

    private void StartGlobalPlannerTask(
        RequirementSet requirementSet,
        ResolutionPolicy resolutionPolicy)
    {
        if (globalPlannerTask != null)
            return;

        var mainCharacterId =
            Plugin.PlayerState.IsLoaded
                ? Plugin.PlayerState.ContentId
                : 0;

        if (mainCharacterId == 0)
            return;

        var requirementSetSnapshot =
            new RequirementSet(
                requirementSet.Name);

        foreach (var requirement in requirementSet.Requirements)
        {
            requirementSetSnapshot.Add(
                requirement);
        }

        var requiredItemIds =
            requirementSetSnapshot.Requirements
                .Select(requirement =>
                    requirement.BaseItemId)
                .ToHashSet();

        var plannerItems =
            plugin.InventoryIndex.Items
                .Where(item =>
                    requiredItemIds.Contains(
                        item.BaseItemId))
                .ToList();

        var stateSnapshot =
            new PlannerState(
                mainCharacterId,
                mainCharacterId,
                plannerItems);

        var resolutionPolicySnapshot =
            new ResolutionPolicy(
                resolutionPolicy.Sources.ToList(),
                mainCharacterId);

        var optimizationSettingsSnapshot =
            new OptimizationSettings(
                optimizationSettings.Criteria.ToList());

        globalPlannerPlan = null;
        globalPlannerError = null;

        globalPlannerResolverMissingSnapshot =
            resolverCachedPlan?.Missing;

        globalPlannerResolverSyncSnapshot =
            plugin.LastSyncAtUtc;

        var planner =
            new GlobalTransferPlanner();

        globalPlannerDiagnostics =
            planner.Diagnostics;

        globalPlannerTask =
            Task.Run(
                () =>
                    planner.Plan(
                        requirementSetSnapshot,
                        stateSnapshot,
                        resolutionPolicySnapshot,
                        optimizationSettingsSnapshot));
    }

    private void TryCompleteGlobalPlannerTask()
    {
        if (globalPlannerTask == null ||
            !globalPlannerTask.IsCompleted)
        {
            return;
        }

        try
        {
            globalPlannerPlan =
                globalPlannerTask
                    .GetAwaiter()
                    .GetResult();

            globalPlannerError = null;
        }
        catch (Exception ex)
        {
            globalPlannerPlan = null;
            globalPlannerError = ex.Message;
        }
        finally
        {
            globalPlannerTask = null;
        }
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
        ResolutionPolicy resolutionPolicy)
    {
        var lines =
            new List<string>
            {
                "FROG DEBUG | RESOLVER CACHE DIAGNOSTICS",
                $"GeneratedUtc={DateTime.UtcNow:O}",
                $"Requirements={requirementSet.Requirements.Count}",
                $"Sources={resolutionPolicy.Sources.Count}",
                $"ComputeCalls={resolverComputeCalls}",
                $"LastElapsedMs={resolverLastElapsedMilliseconds:F6}",
                $"LastAllocatedBytes={resolverLastAllocatedBytes}",
                $"PeakAllocatedBytes={resolverPeakAllocatedBytes}",
                $"TotalAllocatedBytes={resolverTotalAllocatedBytes}"
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
        var lines =
            new List<string>
            {
                "FROG DEBUG | GLOBAL TRANSFER PLANNER",
                $"GeneratedUtc={DateTime.UtcNow:O}",
                $"MainCharacterId={(Plugin.PlayerState.IsLoaded ? Plugin.PlayerState.ContentId : 0)}",
                $"Requirements={requirementSet.Requirements.Count}",
                $"Result={plan.Result}",
                $"Missing={plan.Missing}",
                $"ResolverMissingSnapshot={(globalPlannerResolverMissingSnapshot.HasValue ? globalPlannerResolverMissingSnapshot.Value : -1)}",
                $"MissingDeltaVsResolver={(globalPlannerResolverMissingSnapshot.HasValue ? plan.Missing - globalPlannerResolverMissingSnapshot.Value : 0)}",
                $"ResolverSyncSnapshotUtc={(globalPlannerResolverSyncSnapshot.HasValue ? globalPlannerResolverSyncSnapshot.Value.ToString("O") : "n/a")}",
                $"CharacterSwitches={plan.CharacterSwitches}",
                $"RetainerAccesses={plan.RetainerAccesses}",
                $"TransferHops={plan.TransferHops}",
                $"Actions={plan.Actions.Count}"
            };

        if (globalPlannerDiagnostics != null)
        {
            var diagnostics =
                globalPlannerDiagnostics.Snapshot();

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

        var actionIndex = 1;

        foreach (var action in plan.Actions)
        {
            if (action.Type == PlannerActionType.SwitchCharacter)
            {
                lines.Add(
                    string.Join(
                        "\t",
                        actionIndex,
                        "SWITCH",
                        GetCharacterName(action.FromCharacterId),
                        action.FromCharacterId,
                        GetCharacterName(action.ToCharacterId),
                        action.ToCharacterId));

                actionIndex++;
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
                    actionIndex,
                    "MOVE",
                    GetItemName(action.BaseItemId),
                    action.BaseItemId,
                    action.IsHq ? "HQ" : "NQ",
                    action.Quantity,
                    GetSourceName(action.Source),
                    action.Source.Storage,
                    action.Source.OwnerId,
                    action.Source.ParentCharacterId,
                    GetContainerName(action.Source),
                    GetSourceName(action.Destination),
                    action.Destination.Storage,
                    action.Destination.OwnerId,
                    action.Destination.ParentCharacterId,
                    GetContainerName(action.Destination)));

            actionIndex++;
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

        var fcSync =
            plugin.GetFreeCompanySyncDiagnostics();

        ImGui.Spacing();
        ImGui.Text("DEBUG FC SYNC");
        ImGui.Separator();

        ImGui.Text(
            $"Ultimo tentativo: {(fcSync.ObservedAtUtc.HasValue ? fcSync.ObservedAtUtc.Value.ToString("HH:mm:ss.fff") : "n/a")} UTC");

        ImGui.Text(
            $"Cassa aperta al sync: {(fcSync.ChestOpen ? "SI" : "NO")}");

        ImGui.Text(
            $"Read succeeded: {(fcSync.ReadSucceeded ? "SI" : "NO")}");

        ImGui.Text(
            $"Source lette: {fcSync.SourceCount}");

        ImGui.Text(
            $"Snapshot FC letti: {fcSync.SnapshotCount}");

        ImGui.Text(
            $"Quantità FC letta: {fcSync.TotalQuantity}");

        if (ImGui.Button("COPIA DEBUG FC SYNC"))
        {
            ImGui.SetClipboardText(
                BuildFreeCompanySyncDiagnosticsClipboardText(
                    fcSync));
        }

        ImGui.SameLine();

        if (ImGui.Button("COPIA STORICO FC SYNC"))
        {
            ImGui.SetClipboardText(
                BuildFreeCompanySyncDiagnosticsHistoryClipboardText(
                    plugin.GetFreeCompanySyncDiagnosticsHistory()));
        }

        ImGui.SameLine();

        if (ImGui.Button("COPIA AUDIT INDEX FC"))
        {
            ImGui.SetClipboardText(
                BuildInventoryIndexAuditClipboardText(
                    plugin.InventoryIndex.AuditHistory));
        }
    }

    private static string BuildFreeCompanySyncDiagnosticsClipboardText(
        FreeCompanySyncDiagnosticsSnapshot diagnostics)
    {
        var lines =
            new List<string>
            {
                "FROG DEBUG | FREE COMPANY SYNC",
                $"GeneratedUtc={DateTime.UtcNow:O}",
                $"ObservedAtUtc={(diagnostics.ObservedAtUtc.HasValue ? diagnostics.ObservedAtUtc.Value.ToString("O") : "n/a")}",
                $"ChestOpen={diagnostics.ChestOpen}",
                $"ReadSucceeded={diagnostics.ReadSucceeded}",
                $"SourceCount={diagnostics.SourceCount}",
                $"SnapshotCount={diagnostics.SnapshotCount}",
                $"TotalQuantity={diagnostics.TotalQuantity}"
            };

        lines.Add(string.Empty);
        lines.Add("===== PAGES =====");

        foreach (var page in diagnostics.Pages
                     .OrderBy(page => page.Container))
        {
            lines.Add(
                string.Join(
                    "\t",
                    page.FreeCompanyId,
                    page.Container,
                    $"BeforeSnapshots={page.BeforeSnapshotCount}",
                    $"BeforeQty={page.BeforeQuantity}",
                    $"ReadSnapshots={page.ReadSnapshotCount}",
                    $"ReadQty={page.ReadQuantity}",
                    $"AfterSnapshots={page.AfterSnapshotCount}",
                    $"AfterQty={page.AfterQuantity}"));
        }

        return string.Join(
            Environment.NewLine,
            lines);
    }

    private string BuildFreeCompanySyncDiagnosticsHistoryClipboardText(
        IReadOnlyList<FreeCompanySyncDiagnosticsSnapshot> history)
    {
        var lines =
            new List<string>
            {
                "FROG DEBUG | FREE COMPANY SYNC HISTORY",
                $"GeneratedUtc={DateTime.UtcNow:O}",
                $"Entries={history.Count}"
            };

        foreach (var diagnostics in history)
        {
            lines.Add(string.Empty);
            lines.Add(
                $"===== SYNC {(diagnostics.ObservedAtUtc.HasValue ? diagnostics.ObservedAtUtc.Value.ToString("O") : "n/a")} =====");

            lines.Add(
                $"ChestOpen={diagnostics.ChestOpen} ReadSucceeded={diagnostics.ReadSucceeded} " +
                $"SourceCount={diagnostics.SourceCount} SnapshotCount={diagnostics.SnapshotCount} " +
                $"TotalQuantity={diagnostics.TotalQuantity}");

            foreach (var page in diagnostics.Pages
                         .OrderBy(page =>
                             page.Container))
            {
                lines.Add(
                    string.Join(
                        "\t",
                        "PAGE",
                        page.FreeCompanyId,
                        page.Container,
                        $"BeforeSnapshots={page.BeforeSnapshotCount}",
                        $"BeforeQty={page.BeforeQuantity}",
                        $"ReadSnapshots={page.ReadSnapshotCount}",
                        $"ReadQty={page.ReadQuantity}",
                        $"AfterSnapshots={page.AfterSnapshotCount}",
                        $"AfterQty={page.AfterQuantity}"));

                foreach (var item in page.ChangedItems)
                {
                    lines.Add(
                        string.Join(
                            "\t",
                            "ITEM",
                            page.Container,
                            GetItemName(item.BaseItemId),
                            item.BaseItemId,
                            item.IsHq ? "HQ" : "NQ",
                            $"BeforeQty={item.BeforeQuantity}",
                            $"ReadQty={item.ReadQuantity}"));
                }
            }
        }

        return string.Join(
            Environment.NewLine,
            lines);
    }

    private static string BuildInventoryIndexAuditClipboardText(
        IReadOnlyList<InventoryIndexAuditEntry> history)
    {
        var lines =
            new List<string>
            {
                "FROG DEBUG | INVENTORY INDEX FC AUDIT",
                $"GeneratedUtc={DateTime.UtcNow:O}",
                $"Entries={history.Count}"
            };

        foreach (var entry in history)
        {
            lines.Add(string.Empty);
            lines.Add(
                $"===== {entry.AtUtc:O} | {entry.Operation} =====");

            var beforeByPage =
                entry.BeforeFreeCompanyPages
                    .ToDictionary(
                        page =>
                            (page.FreeCompanyId, page.Container));

            var afterByPage =
                entry.FreeCompanyPages
                    .ToDictionary(
                        page =>
                            (page.FreeCompanyId, page.Container));

            foreach (var key in beforeByPage.Keys
                         .Union(afterByPage.Keys)
                         .OrderBy(key =>
                             key.FreeCompanyId)
                         .ThenBy(key =>
                             key.Container))
            {
                beforeByPage.TryGetValue(
                    key,
                    out var before);

                afterByPage.TryGetValue(
                    key,
                    out var after);

                lines.Add(
                    string.Join(
                        "\t",
                        key.FreeCompanyId,
                        key.Container,
                        $"BeforeSnapshots={before?.SnapshotCount ?? 0}",
                        $"BeforeQty={before?.Quantity ?? 0}",
                        $"AfterSnapshots={after?.SnapshotCount ?? 0}",
                        $"AfterQty={after?.Quantity ?? 0}"));
            }
        }

        return string.Join(
            Environment.NewLine,
            lines);
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

    private IReadOnlyList<InventorySource> BuildPlannerSources(
        IReadOnlyList<InventoryItemSnapshot> indexItems,
        ulong mainCharacterId)
    {
        var sources =
            new List<InventorySource>();

        var allowedCharacterIds =
            GetPlannerCharacterIds(
                    mainCharacterId)
                .ToHashSet();

        if (allowedCharacterIds.Count == 0)
            allowedCharacterIds.Add(mainCharacterId);

        var mainFreeCompanyId =
            GetCharacterFreeCompanyId(
                mainCharacterId);

        foreach (var snapshot in indexItems)
        {
            var source =
                CreateSource(snapshot);

            if (!IsPlannerSourceAllowed(
                    source,
                    allowedCharacterIds,
                    mainFreeCompanyId))
            {
                continue;
            }

            AddPlannerSource(
                sources,
                source);
        }

        foreach (var characterId in allowedCharacterIds)
        {
            AddCharacterInventoryDestination(
                sources,
                characterId);
        }

        if (mainFreeCompanyId != 0)
        {
            AddFreeCompanyHub(
                sources,
                mainFreeCompanyId);
        }

        return sources;
    }

    private IEnumerable<ulong> GetPlannerCharacterIds(
        ulong mainCharacterId)
    {
        yield return mainCharacterId;

        var mainFreeCompanyId =
            GetCharacterFreeCompanyId(
                mainCharacterId);

        if (mainFreeCompanyId == 0)
            yield break;

        foreach (var identity in characterCatalog.Entries)
        {
            if (identity.Type != CharacterIdentityType.Character)
                continue;

            if (identity.CharacterId == 0 ||
                identity.CharacterId == mainCharacterId)
            {
                continue;
            }

            if (identity.FreeCompanyId != mainFreeCompanyId)
                continue;

            yield return identity.CharacterId;
        }
    }

    private ulong GetCharacterFreeCompanyId(
        ulong characterId)
    {
        if (characterCatalog.TryGet(
                characterId,
                out var identity) &&
            identity.Type == CharacterIdentityType.Character &&
            identity.FreeCompanyId != 0)
        {
            return identity.FreeCompanyId;
        }

        return characterMonitor
            .GetCharacterById(characterId)
            ?.FreeCompanyId ?? 0;
    }

    private static bool IsPlannerSourceAllowed(
        InventorySource source,
        IReadOnlySet<ulong> allowedCharacterIds,
        ulong mainFreeCompanyId)
    {
        return source.Storage switch
        {
            StorageType.CharacterInventory =>
                allowedCharacterIds.Contains(
                    source.OwnerId),

            StorageType.Retainer =>
                allowedCharacterIds.Contains(
                    source.ParentCharacterId),

            StorageType.FreeCompanyChest =>
                mainFreeCompanyId != 0 &&
                source.OwnerId == mainFreeCompanyId,

            _ =>
                false
        };
    }

    private void AddCharacterInventoryDestination(
        List<InventorySource> sources,
        ulong characterId)
    {
        var ownerName =
            GetCharacterName(characterId);

        AddPlannerSource(
            sources,
            new InventorySource(
                StorageType.CharacterInventory,
                characterId,
                (uint)GameInventoryType.Inventory1,
                OwnerName: ownerName));
    }

    private void AddFreeCompanyHub(
        List<InventorySource> sources,
        ulong freeCompanyId)
    {
        var freeCompanyName =
            characterCatalog.GetName(
                freeCompanyId);

        if (string.IsNullOrWhiteSpace(
                freeCompanyName))
        {
            freeCompanyName =
                GetFreeCompanyOwnerName(
                    freeCompanyId);
        }

        AddPlannerSource(
            sources,
            new InventorySource(
                StorageType.FreeCompanyChest,
                freeCompanyId,
                (uint)GameInventoryType.FreeCompanyPage1,
                ParentCharacterId: 0,
                OwnerName: freeCompanyName));
    }

    private static void AddPlannerSource(
        List<InventorySource> sources,
        InventorySource source)
    {
        var existingIndex =
            sources.FindIndex(existing =>
                existing.Storage == source.Storage &&
                existing.OwnerId == source.OwnerId &&
                existing.Container == source.Container &&
                existing.ParentCharacterId == source.ParentCharacterId);

        if (existingIndex < 0)
        {
            sources.Add(source);
            return;
        }

        if (string.IsNullOrWhiteSpace(
                sources[existingIndex].OwnerName) &&
            !string.IsNullOrWhiteSpace(
                source.OwnerName))
        {
            sources[existingIndex] = source;
        }
    }

    private InventorySource CreateSource(
        InventoryItemSnapshot snapshot)
    {
        var parentCharacterId =
            snapshot.ParentCharacterId;

        if (snapshot.Storage == StorageType.FreeCompanyChest)
        {
            parentCharacterId = 0;
        }
        else if (parentCharacterId == 0 &&
                 snapshot.Storage == StorageType.Retainer)
        {
            parentCharacterId =
                characterCatalog.GetParentCharacterId(
                    snapshot.OwnerId);
        }

        if (parentCharacterId == 0 &&
            snapshot.Storage == StorageType.Retainer)
        {
            parentCharacterId =
                characterMonitor
                    .GetParentCharacterById(
                        snapshot.OwnerId)
                    ?.CharacterId ?? 0;
        }

        var ownerName =
            GetSnapshotOwnerName(snapshot);

        return new InventorySource(
            snapshot.Storage,
            snapshot.OwnerId,
            snapshot.Container,
            parentCharacterId,
            ownerName);
    }

    private string GetSnapshotOwnerName(
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
            10000 => "Retainer Page 1",
            10001 => "Retainer Page 2",
            10002 => "Retainer Page 3",
            10003 => "Retainer Page 4",
            10004 => "Retainer Page 5",
            10005 => "Retainer Page 6",
            10006 => "Retainer Page 7",

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
