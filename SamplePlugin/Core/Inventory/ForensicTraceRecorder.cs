using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace FROG.Core.Inventory;

/// <summary>
/// Passive, opt-in forensic recorder for reproducing planner/execution bugs.
/// It never drives domain state and never polls game state on its own.
/// </summary>
public static class ForensicTraceRecorder
{
    private static readonly object Sync = new();
    private static readonly StringBuilder Buffer = new();
    private static readonly Stopwatch Elapsed = new();
    private static StreamWriter? writer;
    private static long sequence;

    public static bool IsActive
    {
        get
        {
            lock (Sync)
                return writer is not null;
        }
    }

    public static string? CurrentFilePath { get; private set; }

    public static string Start()
    {
        lock (Sync)
        {
            StopWriter();

            Buffer.Clear();
            sequence = 0;
            Elapsed.Restart();

            var directory = Path.Combine(
                Path.GetTempPath(),
                "FROG",
                "ForensicLogs");

            Directory.CreateDirectory(directory);

            CurrentFilePath = Path.Combine(
                directory,
                $"frog-forensic-{DateTime.Now:yyyyMMdd-HHmmss}.log");

            writer = new StreamWriter(
                CurrentFilePath,
                append: false,
                Encoding.UTF8)
            {
                AutoFlush = false
            };

            WriteCore(
                "TRACE",
                $"START local={DateTime.Now:O} utc={DateTime.UtcNow:O}");

            return CurrentFilePath;
        }
    }

    public static string StopAndGetText()
    {
        lock (Sync)
        {
            if (writer is null)
                return Buffer.ToString();

            WriteCore(
                "TRACE",
                $"STOP local={DateTime.Now:O} utc={DateTime.UtcNow:O}");

            Elapsed.Stop();
            StopWriter();

            return Buffer.ToString();
        }
    }

    public static void Record(
        string category,
        string message)
    {
        lock (Sync)
        {
            if (writer is null)
                return;

            WriteCore(category, message);
        }
    }

    public static void RecordRequirements(
        string reason,
        RequirementSet requirements)
    {
        if (!IsActive)
            return;

        var builder = new StringBuilder();
        builder.AppendLine(
            $"{reason} name={requirements.Name} count={requirements.Requirements.Count}");

        foreach (var requirement in requirements.Requirements)
        {
            builder.AppendLine(
                $"REQ item={requirement.BaseItemId} qty={requirement.Quantity} quality={requirement.QualityPolicy} precraft={requirement.IsPrecraft} untradable={requirement.IsUntradable}");
        }

        Record("REQUIREMENTS", builder.ToString().TrimEnd());
    }

    public static void RecordInventorySnapshot(
        string reason,
        IEnumerable<InventoryItemSnapshot> snapshots)
    {
        if (!IsActive)
            return;

        var materialized = snapshots
            .OrderBy(item => item.Storage)
            .ThenBy(item => item.OwnerId)
            .ThenBy(item => item.Container)
            .ThenBy(item => item.Slot)
            .ToArray();

        var builder = new StringBuilder();

        builder.AppendLine(
            $"{reason} stacks={materialized.Length} totalQty={materialized.Sum(item => (long)item.Quantity)} logicalKeys={materialized.GroupBy(item => new { item.BaseItemId, item.IsHq, item.Storage, item.OwnerId, item.ParentCharacterId }).Count()}");

        foreach (var item in materialized)
        {
            builder.AppendLine(FormatItem(item));
        }

        Record("INVENTORY_SNAPSHOT", builder.ToString().TrimEnd());
    }

    public static void RecordInventoryMutation(
        string operation,
        InventorySource source,
        IReadOnlyList<InventoryItemSnapshot> before,
        IReadOnlyList<InventoryItemSnapshot> after,
        long? observationRevision = null,
        long? contentRevision = null,
        long? providerRevision = null)
    {
        if (!IsActive)
            return;

        var builder = new StringBuilder();

        builder.AppendLine(
            $"{operation} source={FormatSource(source)} beforeStacks={before.Count} beforeQty={before.Sum(item => (long)item.Quantity)} afterStacks={after.Count} afterQty={after.Sum(item => (long)item.Quantity)} obsRev={observationRevision?.ToString() ?? "-"} contentRev={contentRevision?.ToString() ?? "-"} providerRev={providerRevision?.ToString() ?? "-"}");

        builder.AppendLine("BEFORE");
        foreach (var item in before.OrderBy(item => item.Container).ThenBy(item => item.Slot))
            builder.AppendLine(FormatItem(item));

        builder.AppendLine("AFTER");
        foreach (var item in after.OrderBy(item => item.Container).ThenBy(item => item.Slot))
            builder.AppendLine(FormatItem(item));

        Record("INVENTORY_MUTATION", builder.ToString().TrimEnd());
    }

    public static void RecordPlan(
        string reason,
        PlannerPlan plan)
    {
        if (!IsActive)
            return;

        var builder = new StringBuilder();

        builder.AppendLine(
            $"{reason} result={plan.Result} decisions={plan.Decisions.Count} actions={plan.Actions.Count} missing={plan.Missing} capacityBlocked={plan.CapacityBlocked} switches={plan.CharacterSwitches} retainerAccesses={plan.RetainerAccesses} hops={plan.TransferHops}");

        for (var index = 0; index < plan.Decisions.Count; index++)
        {
            var decision = plan.Decisions[index];

            builder.AppendLine(
                $"DECISION {index + 1}/{plan.Decisions.Count} type={decision.Type} item={decision.BaseItemId} hq={decision.IsHq} qty={decision.Quantity} source={FormatSource(decision.Source)} destination={FormatSource(decision.Destination)} switch={decision.FromCharacterId}->{decision.ToCharacterId}");
        }

        Record("PLAN", builder.ToString().TrimEnd());
    }

    public static void RecordExecutionSnapshot(
        string reason,
        PlanExecutionRuntimeSnapshot snapshot)
    {
        if (!IsActive)
            return;

        var session = snapshot.Session;
        var instruction = snapshot.CurrentInstruction;
        var builder = new StringBuilder();

        builder.AppendLine(
            $"{reason} status={snapshot.Status} replanning={snapshot.IsReplanning} error={snapshot.Error ?? "-"} verifiedHistory={snapshot.VerifiedHistory.Count} session={(session is null ? "none" : $"{session.VerifiedDecisionCount}/{session.TotalDecisionCount}")}");

        if (session?.CurrentDecision is { } decision)
        {
            builder.AppendLine(
                $"CURRENT_DECISION index={session.CurrentDecisionIndex} executed={session.IsCurrentDecisionExecuted} type={decision.Type} item={decision.BaseItemId} hq={decision.IsHq} qty={decision.Quantity} source={FormatSource(decision.Source)} destination={FormatSource(decision.Destination)}");
        }

        if (instruction is not null)
        {
            builder.AppendLine(
                $"INSTRUCTION remaining={instruction.RemainingQuantity} allocations={instruction.SourceStacks.Count}");

            foreach (var allocation in instruction.SourceStacks)
            {
                builder.AppendLine(
                    $"ALLOC qty={allocation.Quantity} {FormatItem(allocation.Item)}");
            }
        }

        if (snapshot.Verification is not null)
            builder.AppendLine($"VERIFICATION {snapshot.Verification}");

        if (snapshot.Reconciliation is not null)
            builder.AppendLine($"RECONCILIATION {snapshot.Reconciliation}");

        Record("EXECUTION", builder.ToString().TrimEnd());
    }

    private static string FormatItem(
        InventoryItemSnapshot item) =>
        $"ITEM base={item.BaseItemId} raw={item.RawItemId} hq={item.IsHq} qty={item.Quantity} storage={item.Storage} owner={item.OwnerId} parent={item.ParentCharacterId} container={item.Container} slot={item.Slot} observed={item.ObservedAtUtc:O} current={item.IsCurrent}";

    private static string FormatSource(
        InventorySource? source) =>
        source is null
            ? "-"
            : $"{source.Storage}/owner={source.OwnerId}/parent={source.ParentCharacterId}/container={source.Container}";

    private static void WriteCore(
        string category,
        string message)
    {
        var id = ++sequence;
        var prefix =
            $"#{id:D8} +{Elapsed.ElapsedMilliseconds,8}ms utc={DateTime.UtcNow:O} thread={Thread.CurrentThread.ManagedThreadId:D2} [{category}] ";

        var normalized =
            message.Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal);

        var lines = normalized.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line =
                i == 0
                    ? prefix + lines[i]
                    : new string(' ', prefix.Length) + lines[i];

            Buffer.AppendLine(line);
            writer!.WriteLine(line);
        }

        if (sequence % 100 == 0)
            writer!.Flush();
    }

    private static void StopWriter()
    {
        if (writer is null)
            return;

        writer.Flush();
        writer.Dispose();
        writer = null;
    }
}
