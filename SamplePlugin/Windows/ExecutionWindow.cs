using CriticalCommonLib.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Inventory;
using Dalamud.Interface.Windowing;
using FROG.Core.Inventory;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
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
    private readonly Plugin plugin;

    public ExecutionWindow(
        PlanExecutionRuntime executionRuntime,
        RetainerDisplayLocator retainerDisplayLocator,
        ICharacterMonitor characterMonitor,
        CharacterCatalog characterCatalog,
        Plugin plugin)
        : base("FROG - Execution###FROGExecution")
    {
        this.executionRuntime = executionRuntime;
        this.retainerDisplayLocator = retainerDisplayLocator;
        this.characterMonitor = characterMonitor;
        this.characterCatalog = characterCatalog;
        this.plugin = plugin;

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

                DrawControls(
                    runtime);
                return;
            }

            if (!string.IsNullOrWhiteSpace(
                    runtime.Error))
            {
                ImGui.TextWrapped(
                    $"Errore esecuzione: {runtime.Error}");

                DrawControls(
                    runtime);
                return;
            }

            ImGui.TextWrapped(
                "Nessuna esecuzione attiva.");

            return;
        }

        ImGui.Text(
            session.TotalActionCount > 0
                ? $"Azione {Math.Min(session.CurrentActionIndex, session.TotalActionCount)} / {session.TotalActionCount}"
                : "Azioni attualmente eseguibili: 0");

        ImGui.ProgressBar(
            session.TotalActionCount > 0
                ? (float)session.VerifiedActionCount /
                  session.TotalActionCount
                : session.IsComplete
                    ? 1f
                    : 0f,
            new Vector2(-1, 0),
            $"{session.VerifiedActionCount}/{session.TotalActionCount}");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (runtime.IsReplanning)
        {
            ImGui.TextWrapped(
                "FROG ha rilevato una variazione rispetto al piano. Ricalcolo automatico del percorso residuo in corso...");

            DrawReconciliation(
                runtime.Reconciliation);

            DrawControls(
                runtime);
            return;
        }

        if (!string.IsNullOrWhiteSpace(
                runtime.Error))
        {
            ImGui.TextWrapped(
                $"Errore esecuzione: {runtime.Error}");

            DrawControls(
                runtime);
            return;
        }

        if (runtime.Status ==
            PlanExecutionCoordinatorStatus.WaitingForCapacity)
        {
            DrawCapacityWait(
                session.Plan);

            DrawControls(
                runtime);
            return;
        }

        if (runtime.Status ==
                PlanExecutionCoordinatorStatus.Complete ||
            session.IsComplete)
        {
            DrawCompletion(
                session.Plan);

            DrawControls(
                runtime);
            return;
        }

        var instruction =
            runtime.CurrentInstruction;

        var decision =
            instruction?.Decision ??
            session.CurrentDecision;

        if (decision is null)
        {
            ImGui.TextWrapped(
                "Nessuna decisione corrente disponibile.");

            DrawControls(
                runtime);
            return;
        }

        if (decision.Type ==
            PlannerDecisionType.SwitchCharacter)
        {
            DrawSwitchAction(
                decision);
        }
        else
        {
            DrawMoveAction(
                decision,
                instruction);
        }

        ImGui.Spacing();

        DrawStatus(
            runtime);

        DrawControls(
            runtime);
    }

    private void DrawSwitchAction(
        PlannerDecision decision)
    {
        ImGui.TextWrapped(
            "CAMBIA PERSONAGGIO");

        ImGui.Spacing();

        ImGui.TextWrapped(
            $"Da: {GetCharacterName(decision.FromCharacterId)}");

        ImGui.TextWrapped(
            $"A: {GetCharacterName(decision.ToCharacterId)}");
    }

    private void DrawMoveAction(
        PlannerDecision decision,
        ExecutionInstruction? instruction)
    {
        if (decision.Source is null ||
            decision.Destination is null)
        {
            ImGui.TextWrapped(
                "Decisione MOVE priva di origine o destinazione.");

            return;
        }

        ImGui.TextWrapped(
            $"{GetItemName(decision.BaseItemId)} | " +
            $"{(decision.IsHq ? "HQ" : "NQ")} | " +
            $"x{decision.Quantity}");

        ImGui.Spacing();

        ImGui.TextWrapped(
            $"PRENDI DA: {GetSourceName(decision.Source)}");

        ImGui.TextWrapped(
            $"DOVE: {GetSourceLocationText(
                decision.Source,
                instruction)}");

        ImGui.Spacing();

        ImGui.TextWrapped(
            $"PORTA A: {GetSourceName(decision.Destination)}");

        ImGui.TextWrapped(
            $"DESTINAZIONE: {GetContainerName(decision.Destination)}");
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

            case PlanExecutionCoordinatorStatus.WaitingForCapacity:
                ImGui.TextWrapped(
                    "In attesa di spazio sufficiente nella destinazione del prossimo movimento.");
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

        ImGui.TextWrapped(
            reconciliation.Message);
    }

    private static void DrawCompletion(
        PlannerPlan plan)
    {
        if (plan.CapacityBlocked > 0)
        {
            ImGui.TextWrapped(
                plan.Decisions.Count == 0
                    ? "Piano non eseguibile: lo spazio libero nelle destinazioni non è sufficiente."
                    : "Tutte le azioni eseguibili sono state osservate e verificate, ma il piano non può essere completato per spazio insufficiente.");

            ImGui.TextWrapped(
                $"Libera almeno uno slot nell'inventario o deposito di destinazione e ricalcola il piano. Quantità bloccata: {plan.CapacityBlocked}.");

            var unavailableMissing =
                plan.Missing -
                plan.CapacityBlocked;

            if (unavailableMissing > 0)
            {
                ImGui.TextWrapped(
                    $"Ulteriori unità non disponibili nelle sorgenti: {unavailableMissing}.");
            }

            return;
        }

        if (plan.Missing > 0)
        {
            ImGui.TextWrapped(
                $"Tutte le azioni disponibili sono state osservate e verificate. Restano {plan.Missing} unità non disponibili nelle sorgenti.");
            return;
        }

        ImGui.TextWrapped(
            plan.Decisions.Count == 0
                ? "Nessuna azione necessaria: il fabbisogno è già soddisfatto."
                : "Piano completato. Tutte le azioni sono state osservate e verificate.");
    }

    private void DrawCapacityWait(
        PlannerPlan plan)
    {
        ImGui.TextWrapped(
            "IN ATTESA DI SPAZIO");

        var capacityBlock =
            plan.NextCapacityBlock;

        if (capacityBlock is null)
        {
            ImGui.TextWrapped(
                $"Spazio insufficiente nelle destinazioni del piano. Quantità bloccata: {plan.CapacityBlocked}.");
            ImGui.TextWrapped(
                "Libera spazio e ricalcola il piano.");
            return;
        }

        foreach (var advice in plan.CapacityAdvice)
        {
            var slotText =
                advice.MinimumAdditionalSlots == 1
                    ? "1 slot"
                    : $"{advice.MinimumAdditionalSlots} slot";

            ImGui.TextWrapped(
                $"Consiglio: libera almeno {slotText} in {GetSourceName(advice.Destination)} ({GetContainerName(advice.Destination)}) per {advice.BlockedQuantity} unità bloccate.");
        }

        ImGui.TextWrapped(
            "I merge compatibili già osservati, HQ/NQ separati e lo slot di sicurezza sono inclusi nel calcolo.");

        ImGui.Spacing();

        ImGui.TextWrapped(
            "MOVIMENTO DA SBLOCCARE");

        ImGui.TextWrapped(
            $"{GetItemName(capacityBlock.BaseItemId)} | " +
            $"{(capacityBlock.IsHq ? "HQ" : "NQ")} | " +
            $"x{capacityBlock.Quantity}");

        ImGui.TextWrapped(
            $"PRENDI DA: {GetSourceName(capacityBlock.Source)}");

        ImGui.TextWrapped(
            $"PORTA A: {GetSourceName(capacityBlock.Destination)}");

        ImGui.TextWrapped(
            "Quando FROG osserverà spazio sufficiente, ricalcolerà automaticamente il piano dallo stato reale.");
    }

    private void DrawControls(
        PlanExecutionRuntimeSnapshot runtime)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("COPIA DEBUG"))
        {
            ImGui.SetClipboardText(
                BuildExecutionDebugClipboardText(
                    runtime));
        }

        ImGui.SameLine();

        if (ImGui.Button("INTERROMPI ESECUZIONE"))
        {
            executionRuntime.Clear();
            IsOpen = false;
        }
    }

    private string BuildExecutionDebugClipboardText(
        PlanExecutionRuntimeSnapshot runtime)
    {
        var lines =
            new List<string>
            {
                "FROG DEBUG | EXECUTION",
                $"GeneratedUtc={DateTime.UtcNow:O}",
                $"Status={runtime.Status}",
                $"IsReplanning={runtime.IsReplanning}",
                $"Error={runtime.Error ?? "n/a"}"
            };

        var session =
            runtime.Session;

        if (session is null)
        {
            lines.Add("SessionActive=False");

            return string.Join(
                Environment.NewLine,
                lines);
        }

        var plan =
            session.Plan;

        lines.Add("SessionActive=True");
        lines.Add($"PlanResult={plan.Result}");
        lines.Add($"Missing={plan.Missing}");
        lines.Add($"CapacityBlocked={plan.CapacityBlocked}");
        lines.Add($"CapacityBlocks={plan.CapacityBlocks.Count}");
        lines.Add($"CapacityAdvice={plan.CapacityAdvice.Count}");
        lines.Add($"UnavailableMissing={Math.Max(0, plan.Missing - plan.CapacityBlocked)}");
        lines.Add($"Actions={session.TotalActionCount}");
        lines.Add($"VerifiedActions={session.VerifiedActionCount}");
        lines.Add($"RemainingActions={session.RemainingActionCount}");
        lines.Add($"CurrentActionIndex={session.CurrentActionIndex}");

        if (runtime.Verification is not null)
        {
            lines.Add($"VerificationStatus={runtime.Verification.Status}");
            lines.Add($"VerificationMessage={runtime.Verification.Message}");
        }

        if (runtime.Reconciliation is not null)
        {
            lines.Add($"PlannedQuantity={runtime.Reconciliation.PlannedQuantity}");
            lines.Add($"SourceDecrease={runtime.Reconciliation.SourceDecrease}");
            lines.Add($"DestinationIncrease={runtime.Reconciliation.DestinationIncrease}");
            lines.Add($"ReconciledQuantity={runtime.Reconciliation.ReconciledQuantity}");
        }

        if (plan.CapacityBlocks.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("===== CAPACITY ADVICE =====");

            foreach (var advice in plan.CapacityAdvice)
            {
                lines.Add(
                    string.Join(
                        "\t",
                        GetSourceName(advice.Destination),
                        advice.Destination.Storage,
                        GetContainerName(advice.Destination),
                        $"MinimumAdditionalSlots={advice.MinimumAdditionalSlots}",
                        $"BlockedQuantity={advice.BlockedQuantity}"));
            }

            lines.Add(string.Empty);
            lines.Add("===== CAPACITY BLOCKS =====");

            for (var blockIndex = 0;
                 blockIndex < plan.CapacityBlocks.Count;
                 blockIndex++)
            {
                var capacityBlock =
                    plan.CapacityBlocks[blockIndex];

                lines.Add(
                    string.Join(
                        "\t",
                        blockIndex + 1,
                        GetItemName(capacityBlock.BaseItemId),
                        capacityBlock.BaseItemId,
                        capacityBlock.IsHq ? "HQ" : "NQ",
                        capacityBlock.Quantity,
                        $"MaximumStack={capacityBlock.MaximumStack}",
                        $"MinimumAdditionalSlots={capacityBlock.MinimumAdditionalSlots}",
                        GetSourceName(capacityBlock.Source),
                        capacityBlock.Source.Storage,
                        GetSourceName(capacityBlock.Destination),
                        capacityBlock.Destination.Storage,
                        GetContainerName(capacityBlock.Destination)));
            }
        }

        lines.Add(string.Empty);
        lines.Add("===== STEPS =====");

        foreach (var step in session.Steps)
        {
            var decision =
                step.Decision;

            if (decision.Type ==
                PlannerDecisionType.SwitchCharacter)
            {
                lines.Add(
                    string.Join(
                        "\t",
                        step.Index,
                        step.Status,
                        "SWITCH",
                        GetCharacterName(
                            decision.FromCharacterId),
                        decision.FromCharacterId,
                        GetCharacterName(
                            decision.ToCharacterId),
                        decision.ToCharacterId));

                continue;
            }

            if (decision.Source is null ||
                decision.Destination is null)
            {
                lines.Add(
                    $"{step.Index}\t{step.Status}\tMOVE\tINVALID");
                continue;
            }

            var instruction =
                step.Index ==
                session.CurrentActionIndex
                    ? runtime.CurrentInstruction
                    : null;

            lines.Add(
                string.Join(
                    "\t",
                    step.Index,
                    step.Status,
                    "MOVE",
                    GetItemName(
                        decision.BaseItemId),
                    decision.BaseItemId,
                    decision.IsHq
                        ? "HQ"
                        : "NQ",
                    decision.Quantity,
                    GetSourceName(
                        decision.Source),
                    decision.Source.Storage,
                    GetSourceLocationText(
                        decision.Source,
                        instruction),
                    GetSourceName(
                        decision.Destination),
                    decision.Destination.Storage,
                    GetContainerName(
                        decision.Destination)));
        }

        return string.Join(
            Environment.NewLine,
            lines);
    }

    private void OnSessionStarted()
    {
        IsOpen = true;
    }

    private string GetSourceLocationText(
        PlannerLogicalSource source,
        ExecutionInstruction? instruction)
    {
        if (instruction is null)
        {
            return GetContainerName(
                source);
        }

        if (source.Storage ==
            StorageType.Retainer)
        {
            var location =
                retainerDisplayLocator.LocateSource(
                    instruction);

            return location.IsAvailable
                ? string.Join(
                    ", ",
                    location.Positions.Select(position =>
                        $"Pagina {position.Page}, slot {position.Slot} x{position.Quantity}"))
                : GetContainerName(
                    source);
        }

        return GetContainerName(
            source);
    }

    private string GetSourceName(
        PlannerLogicalSource source)
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
        PlannerLogicalSource source) =>
        source.Storage switch
        {
            StorageType.CharacterInventory =>
                "Inventario personaggio",

            StorageType.Retainer =>
                "Inventario retainer",

            StorageType.FreeCompanyChest =>
                "Free Company Chest",

            _ =>
                source.Storage.ToString()
        };

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
                    InventorySource.AnyFreeCompanyPageContainer =>
                        "FC Chest - qualsiasi pagina",
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
