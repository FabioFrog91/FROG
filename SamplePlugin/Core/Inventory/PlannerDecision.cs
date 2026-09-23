using System;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

public enum PlannerDecisionType
{
    Move,
    SwitchCharacter
}

public sealed record PlannerLogicalSource(
    StorageType Storage,
    ulong OwnerId,
    ulong ParentCharacterId,
    string? OwnerName = null);

public sealed record PlannerDecision(
    PlannerDecisionType Type,
    PlannerLogicalSource? Source,
    PlannerLogicalSource? Destination,
    uint BaseItemId,
    bool IsHq,
    int Quantity,
    ulong FromCharacterId,
    ulong ToCharacterId)
{
    public static PlannerDecision Move(
        PlannerLogicalSource source,
        PlannerLogicalSource destination,
        uint baseItemId,
        bool isHq,
        int quantity) =>
        new(
            PlannerDecisionType.Move,
            source,
            destination,
            baseItemId,
            isHq,
            quantity,
            0,
            0);

    public static PlannerDecision SwitchCharacter(
        ulong fromCharacterId,
        ulong toCharacterId) =>
        new(
            PlannerDecisionType.SwitchCharacter,
            null,
            null,
            0,
            false,
            0,
            fromCharacterId,
            toCharacterId);
}

internal static class PlannerDecisionCompiler
{
    public static IReadOnlyList<PlannerDecision> Compile(
        IReadOnlyList<PlannerAction> actions)
    {
        var result =
            new List<PlannerDecision>();

        var index = 0;

        while (index < actions.Count)
        {
            var action =
                actions[index];

            if (action.Type ==
                PlannerActionType.SwitchCharacter)
            {
                result.Add(
                    PlannerDecision.SwitchCharacter(
                        action.FromCharacterId,
                        action.ToCharacterId));

                index++;
                continue;
            }

            if (action.Source is null ||
                action.Destination is null)
            {
                throw new InvalidOperationException(
                    "Planner MOVE is missing source or destination.");
            }

            var groupEnd =
                index + 1;

            while (groupEnd < actions.Count &&
                   actions[groupEnd].Type ==
                       PlannerActionType.Move &&
                   IsSameLogicalRoute(
                       action,
                       actions[groupEnd]))
            {
                groupEnd++;
            }

            var groupedMoves =
                actions
                    .Skip(index)
                    .Take(groupEnd - index)
                    .Select((move, originalIndex) =>
                        new
                        {
                            Move = move,
                            OriginalIndex = originalIndex
                        })
                    .GroupBy(entry =>
                        new
                        {
                            entry.Move.BaseItemId,
                            entry.Move.IsHq
                        })
                    .OrderBy(group =>
                        group.Min(entry =>
                            entry.OriginalIndex));

            foreach (var group in groupedMoves)
            {
                var first =
                    group.First().Move;

                result.Add(
                    PlannerDecision.Move(
                        ToLogicalSource(
                            first.Source!),
                        ToLogicalSource(
                            first.Destination!),
                        group.Key.BaseItemId,
                        group.Key.IsHq,
                        checked(
                            group.Sum(entry =>
                                entry.Move.Quantity))));
            }

            index =
                groupEnd;
        }

        return result;
    }

    private static bool IsSameLogicalRoute(
        PlannerAction first,
        PlannerAction candidate)
    {
        if (first.Source is null ||
            first.Destination is null ||
            candidate.Source is null ||
            candidate.Destination is null)
        {
            return false;
        }

        return IsSameLogicalSource(
                   first.Source,
                   candidate.Source) &&
               IsSameLogicalSource(
                   first.Destination,
                   candidate.Destination);
    }

    private static bool IsSameLogicalSource(
        InventorySource first,
        InventorySource candidate) =>
        first.Storage ==
            candidate.Storage &&
        first.OwnerId ==
            candidate.OwnerId &&
        first.ParentCharacterId ==
            candidate.ParentCharacterId;

    private static PlannerLogicalSource ToLogicalSource(
        InventorySource source) =>
        new(
            source.Storage,
            source.OwnerId,
            source.ParentCharacterId,
            source.OwnerName);
}
