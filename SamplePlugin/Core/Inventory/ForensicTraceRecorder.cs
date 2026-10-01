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
    private static string? lastExecutionFingerprint;
    private static readonly Dictionary<string, string> LastStateByKey = new();
    private static readonly HashSet<uint> RelevantItemIds = new();

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
            lastExecutionFingerprint = null;
            LastStateByKey.Clear();
            RelevantItemIds.Clear();
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

    public static void StopWithoutCopy(
        string reason)
    {
        lock (Sync)
        {
            if (writer is null)
                return;

            WriteCore(
                "TRACE",
                $"STOP_WITHOUT_COPY reason={reason} local={DateTime.Now:O} utc={DateTime.UtcNow:O}");

            Elapsed.Stop();
            StopWriter();
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

    public static void RecordState(
        string category,
        string key,
        string message)
    {
        lock (Sync)
        {
            if (writer is null)
                return;

            if (LastStateByKey.TryGetValue(key, out var previous) &&
                previous == message)
            {
                return;
            }

            LastStateByKey[key] = message;
            WriteCore(category, message);
        }
    }

    public static void RecordRequirements(
        string reason,
        RequirementSet requirements)
    {
        if (!IsActive)
            return;

        lock (Sync)
        {
            if (writer is null)
                return;

            RelevantItemIds.Clear();
            foreach (var requirement in requirements.Requirements)
                RelevantItemIds.Add(requirement.BaseItemId);
        }

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

        HashSet<uint> relevantIds;
        lock (Sync)
            relevantIds = RelevantItemIds.ToHashSet();

        var relevant = materialized
            .Where(item => relevantIds.Contains(item.BaseItemId))
            .ToArray();

        var builder = new StringBuilder();

        builder.AppendLine(
            $"{reason} stacks={materialized.Length} totalQty={materialized.Sum(item => (long)item.Quantity)} logicalKeys={materialized.GroupBy(item => new { item.BaseItemId, item.IsHq, item.Storage, item.OwnerId, item.ParentCharacterId }).Count()} relevantStacks={relevant.Length} relevantItemIds={relevantIds.Count}");

        foreach (var source in materialized
                     .GroupBy(item => new
                     {
                         item.Storage,
                         item.OwnerId,
                         item.ParentCharacterId,
                         item.Container
                     })
                     .OrderBy(group => group.Key.Storage)
                     .ThenBy(group => group.Key.OwnerId)
                     .ThenBy(group => group.Key.Container))
        {
            builder.AppendLine(
                $"SOURCE storage={source.Key.Storage} owner={source.Key.OwnerId} parent={source.Key.ParentCharacterId} container={source.Key.Container} stacks={source.Count()} qty={source.Sum(item => (long)item.Quantity)}");
        }

        foreach (var item in relevant)
            builder.AppendLine(FormatItem(item));

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

        if (!operation.EndsWith("_UNCHANGED", StringComparison.Ordinal))
        {
            var beforeBySlot = before.ToDictionary(item => item.Slot);
            var afterBySlot = after.ToDictionary(item => item.Slot);

            foreach (var slot in beforeBySlot.Keys
                         .Union(afterBySlot.Keys)
                         .OrderBy(value => value))
            {
                var hasBefore = beforeBySlot.TryGetValue(slot, out var beforeItem);
                var hasAfter = afterBySlot.TryGetValue(slot, out var afterItem);

                if (hasBefore && !hasAfter)
                {
                    builder.AppendLine($"REMOVED {FormatItem(beforeItem!)}");
                    continue;
                }

                if (!hasBefore && hasAfter)
                {
                    builder.AppendLine($"ADDED {FormatItem(afterItem!)}");
                    continue;
                }

                if (hasBefore &&
                    hasAfter &&
                    !SamePhysicalStack(beforeItem!, afterItem!))
                {
                    builder.AppendLine(
                        $"CHANGED before=[{FormatItem(beforeItem!)}] after=[{FormatItem(afterItem!)}]");
                }
            }
        }

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

    public static void RecordExecutionSnapshotIfChanged(
        string reason,
        PlanExecutionRuntimeSnapshot snapshot)
    {
        if (!IsActive)
            return;

        var session = snapshot.Session;
        var instruction = snapshot.CurrentInstruction;

        var allocationFingerprint =
            instruction is null
                ? "-"
                : string.Join(
                    ";",
                    instruction.SourceStacks.Select(allocation =>
                        $"{allocation.Item.Storage}:{allocation.Item.OwnerId}:{allocation.Item.Container}:{allocation.Item.Slot}:{allocation.Item.BaseItemId}:{allocation.Item.IsHq}:{allocation.Quantity}"));

        var fingerprint =
            $"{snapshot.Status}|{snapshot.IsReplanning}|{snapshot.Error}|{session?.VerifiedDecisionCount}|{session?.CurrentDecisionIndex}|{session?.IsCurrentDecisionExecuted}|{instruction?.RemainingQuantity}|{allocationFingerprint}|{snapshot.Verification}|{snapshot.Reconciliation}|{snapshot.VerifiedHistory.Count}";

        lock (Sync)
        {
            if (writer is null ||
                fingerprint == lastExecutionFingerprint)
            {
                return;
            }

            lastExecutionFingerprint = fingerprint;
        }

        RecordExecutionSnapshot(reason, snapshot);
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

    private static bool SamePhysicalStack(
        InventoryItemSnapshot left,
        InventoryItemSnapshot right) =>
        left.BaseItemId == right.BaseItemId &&
        left.RawItemId == right.RawItemId &&
        left.IsHq == right.IsHq &&
        left.Quantity == right.Quantity &&
        left.Storage == right.Storage &&
        left.OwnerId == right.OwnerId &&
        left.ParentCharacterId == right.ParentCharacterId &&
        left.Container == right.Container &&
        left.Slot == right.Slot &&
        left.IsVerified == right.IsVerified;

    private static string FormatItem(
        InventoryItemSnapshot item) =>
        $"ITEM base={item.BaseItemId} raw={item.RawItemId} hq={item.IsHq} qty={item.Quantity} storage={item.Storage} owner={item.OwnerId} parent={item.ParentCharacterId} container={item.Container} slot={item.Slot} observed={item.ObservedAtUtc:O} verified={item.IsVerified}";

    private static string FormatSource(
        InventorySource? source) =>
        source is null
            ? "-"
            : $"{source.Storage}/owner={source.OwnerId}/parent={source.ParentCharacterId}/container={source.Container}";

    private static string FormatSource(
        PlannerLogicalSource? source) =>
        source is null
            ? "-"
            : $"{source.Storage}/owner={source.OwnerId}/parent={source.ParentCharacterId}";

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
                    : "  " + lines[i];

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
