using CriticalCommonLib.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Inventory;
using Dalamud.Interface.Windowing;
using FROG.Core.Inventory;
using Lumina.Excel.Sheets;
using System;
using System.Linq;
using System.Numerics;

namespace FROG.Windows;

/// <summary>
/// Compact, passive execution guide. All state transitions are owned by
/// PlanExecutionRuntime; this window only renders its latest snapshot.
/// </summary>
public sealed class ExecutionWindow : Window, IDisposable
{
    private readonly PlanExecutionRuntime executionRuntime;
    private readonly RetainerDisplayLocator retainerDisplayLocator;
    private readonly ICharacterMonitor characterMonitor;
    private readonly CharacterCatalog characterCatalog;

    public ExecutionWindow(
        PlanExecutionRuntime executionRuntime,
        RetainerDisplayLocator retainerDisplayLocator,
        ICharacterMonitor characterMonitor,
        CharacterCatalog characterCatalog)
        : base("FROG - Execution###FROGExecution")
    {
        this.executionRuntime = executionRuntime;
        this.retainerDisplayLocator = retainerDisplayLocator;
        this.characterMonitor = characterMonitor;
        this.characterCatalog = characterCatalog;

        Size = new Vector2(430, 245);
        SizeCondition = ImGuiCond.FirstUseEver;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(360, 190),
            MaximumSize = new Vector2(700, 520)
        };

        executionRuntime.SessionStarted +=
            OnSessionStarted;
    }

    public void Dispose()
    {
        executionRuntime.SessionStarted -=
            OnSessionStarted;
    }

    public override void Draw()
    {
        var runtime =
            executionRuntime.Snapshot;

        var session =
            runtime.Session;

        if (session is null)
        {
            if (runtime.IsReplanning)
            {
                ImGui.TextWrapped(
                    "Il piano precedente non è più valido. FROG sta ricalcolando automaticamente il percorso dallo stato reale.");

                DrawControls();
                return;
            }

            if (!string.IsNullOrWhiteSpace(
                    runtime.Error))
            {
                ImGui.TextWrapped(
                    $"Errore esecuzione: {runtime.Error}");

                DrawControls();
                return;
            }

            ImGui.TextWrapped(
                "Nessuna esecuzione attiva.");

            return;
        }

        ImGui.Text(
            $"Azione {Math.Min(session.CurrentActionIndex, session.TotalActionCount)} / {session.TotalActionCount}");

        ImGui.ProgressBar(
            session.TotalActionCount > 0
                ? (float)session.VerifiedActionCount /
                  session.TotalActionCount
                : 0f,
            new Vector2(-1, 0),
            $"{session.VerifiedActionCount}/{session.TotalActionCount}");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (runtime.IsReplanning)
        {
            ImGui.TextWrapped(
                "FROG ha rilevato una quantità diversa dal piano. Ricalcolo automatico del percorso residuo in corso...");

            DrawReconciliation(
                runtime.Reconciliation);

            DrawControls();
            return;
        }

        if (!string.IsNullOrWhiteSpace(
                runtime.Error))
        {
            ImGui.TextWrapped(
                $"Errore esecuzione: {runtime.Error}");

            DrawControls();
            return;
        }

        if (runtime.Status ==
                PlanExecutionCoordinatorStatus.Complete ||
            session.IsComplete)
        {
            ImGui.TextWrapped(
                "Piano completato. Tutte le azioni sono state osservate e verificate.");

            DrawControls();
            return;
        }

        var action =
            runtime.CurrentAction ??
            session.CurrentAction;

        if (action is null)
        {
            ImGui.TextWrapped(
                "Nessuna azione corrente disponibile.");

            DrawControls();
            return;
        }

        if (action.Type ==
            PlannerActionType.SwitchCharacter)
        {
            DrawSwitchAction(
                action);
        }
        else
        {
            DrawMoveAction(
                session.Plan,
                session.CurrentActionIndex - 1,
                action);
        }

        ImGui.Spacing();

        DrawStatus(
            runtime);

        DrawControls();
    }

    private void DrawSwitchAction(
        PlannerAction action)
    {
        ImGui.TextWrapped(
            "CAMBIA PERSONAGGIO");

        ImGui.Spacing();

        ImGui.TextWrapped(
            $"Da: {GetCharacterName(action.FromCharacterId)}");

        ImGui.TextWrapped(
            $"A: {GetCharacterName(action.ToCharacterId)}");
    }

    private void DrawMoveAction(
        PlannerPlan plan,
        int actionIndex,
        PlannerAction action)
    {
        if (action.Source is null ||
            action.Destination is null)
        {
            ImGui.TextWrapped(
                "Azione MOVE priva di origine o destinazione.");

            return;
        }

        ImGui.TextWrapped(
            $"{GetItemName(action.BaseItemId)} | " +
            $"{(action.IsHq ? "HQ" : "NQ")} | " +
            $"x{action.Quantity}");

        ImGui.Spacing();

        ImGui.TextWrapped(
            $"PRENDI DA: {GetSourceName(action.Source)}");

        ImGui.TextWrapped(
            $"DOVE: {GetSourceLocationText(plan, actionIndex, action.Source)}");

        ImGui.Spacing();

        ImGui.TextWrapped(
            $"PORTA A: {GetSourceName(action.Destination)}");

        ImGui.TextWrapped(
            $"DESTINAZIONE: {GetContainerName(action.Destination)}");
    }

    private static void DrawStatus(
        PlanExecutionRuntimeSnapshot runtime)
    {
        switch (runtime.Status)
        {
            case PlanExecutionCoordinatorStatus.Pending:
                ImGui.TextWrapped(
                    "In attesa dell'azione. FROG passerà automaticamente alla successiva quando osserverà il trasferimento previsto.");
                break;

            case PlanExecutionCoordinatorStatus.Executed:
            case PlanExecutionCoordinatorStatus.WaitingForObservation:
                ImGui.TextWrapped(
                    "Movimento rilevato. In attesa della verifica completa di origine e destinazione.");
                break;

            case PlanExecutionCoordinatorStatus.Verified:
                ImGui.TextWrapped(
                    "Azione verificata. Passaggio automatico alla successiva.");
                break;

            case PlanExecutionCoordinatorStatus.ReplanRequired:
                ImGui.TextWrapped(
                    "Differenza rilevata. Preparazione del nuovo piano residuo.");
                break;
        }

        if (runtime.Verification is not null &&
            runtime.Status !=
                PlanExecutionCoordinatorStatus.Pending)
        {
            ImGui.TextWrapped(
                runtime.Verification.Message);
        }

        DrawReconciliation(
            runtime.Reconciliation);
    }

    private static void DrawReconciliation(
        PlanExecutionReconciliationResult? reconciliation)
    {
        if (reconciliation is null)
            return;

        ImGui.TextWrapped(
            $"Osservato: source -{reconciliation.SourceDecrease}, " +
            $"destination +{reconciliation.DestinationIncrease}. " +
            $"Confermato {reconciliation.ReconciledQuantity}/{reconciliation.PlannedQuantity}.");
    }

    private void DrawControls()
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("INTERROMPI ESECUZIONE"))
        {
            executionRuntime.Clear();
            IsOpen = false;
        }
    }

    private void OnSessionStarted()
    {
        IsOpen = true;
    }

    private string GetSourceLocationText(
        PlannerPlan plan,
        int actionIndex,
        InventorySource source)
    {
        if (source.Storage !=
            StorageType.Retainer)
        {
            return GetContainerName(
                source);
        }

        var displayLocation =
            retainerDisplayLocator.LocateSource(
                plan,
                actionIndex);

        if (!displayLocation.IsAvailable)
        {
            return GetContainerName(
                source);
        }

        return string.Join(
            ", ",
            displayLocation.Positions
                .Select(position =>
                    $"Pagina {position.Page}, slot {position.Slot} x{position.Quantity}"));
    }

    private string GetSourceName(
        InventorySource source)
    {
        var ownerName =
            source.OwnerName;

        if (!string.IsNullOrWhiteSpace(
                ownerName))
        {
            return source.Storage ==
                       StorageType.Retainer
                ? AppendParentCharacter(
                    ownerName,
                    source.ParentCharacterId)
                : ownerName;
        }

        return source.Storage switch
        {
            StorageType.CharacterInventory =>
                GetCharacterName(
                    source.OwnerId),

            StorageType.Retainer =>
                AppendParentCharacter(
                    GetCharacterName(
                        source.OwnerId),
                    source.ParentCharacterId),

            StorageType.FreeCompanyChest =>
                GetCharacterName(
                    source.OwnerId),

            _ =>
                source.Storage.ToString()
        };
    }

    private string AppendParentCharacter(
        string sourceName,
        ulong parentCharacterId)
    {
        if (parentCharacterId == 0)
            return sourceName;

        var parentName =
            GetCharacterName(
                parentCharacterId);

        return string.IsNullOrWhiteSpace(
            parentName)
            ? sourceName
            : $"{sourceName} ({parentName})";
    }

    private string GetCharacterName(
        ulong characterId)
    {
        var catalogName =
            characterCatalog.GetName(
                characterId);

        if (!string.IsNullOrWhiteSpace(
                catalogName))
        {
            return catalogName;
        }

        var monitorName =
            characterMonitor.GetCharacterNameById(
                characterId);

        return string.IsNullOrWhiteSpace(
            monitorName)
            ? $"ID {characterId}"
            : monitorName;
    }

    private static string GetItemName(
        uint baseItemId)
    {
        var sheet =
            Plugin.DataManager.GetExcelSheet<Item>();

        if (sheet.TryGetRow(
                baseItemId,
                out var item))
        {
            var name =
                item.Name.ToString();

            if (!string.IsNullOrWhiteSpace(
                    name))
            {
                return name;
            }
        }

        return $"Item {baseItemId}";
    }

    private static string GetContainerName(
        InventorySource source)
    {
        return source.Storage switch
        {
            StorageType.CharacterInventory =>
                "Inventario personaggio",

            StorageType.Retainer =>
                source.Container switch
                {
                    10000 => "Retainer Internal Container 1",
                    10001 => "Retainer Internal Container 2",
                    10002 => "Retainer Internal Container 3",
                    10003 => "Retainer Internal Container 4",
                    10004 => "Retainer Internal Container 5",
                    10005 => "Retainer Internal Container 6",
                    10006 => "Retainer Internal Container 7",
                    _ => $"Retainer Container {source.Container}"
                },

            StorageType.FreeCompanyChest =>
                source.Container switch
                {
                    20000 => "FC Chest Page 1",
                    20001 => "FC Chest Page 2",
                    20002 => "FC Chest Page 3",
                    20003 => "FC Chest Page 4",
                    20004 => "FC Chest Page 5",
                    _ => $"FC Chest Container {source.Container}"
                },

            _ =>
                $"Container {source.Container}"
        };
    }
}
