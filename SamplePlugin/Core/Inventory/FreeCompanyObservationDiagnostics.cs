using System;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

public static class FreeCompanyObservationDiagnostics
{
    private const int MaxEntries = 500;

    private static readonly object Sync = new();
    private static readonly Queue<string> Entries = new();

    public static void Add(string message)
    {
        var line =
            $"{DateTime.Now:HH:mm:ss.fff} | {message}";

        lock (Sync)
        {
            Entries.Enqueue(line);

            while (Entries.Count > MaxEntries)
            {
                Entries.Dequeue();
            }
        }
    }

    public static IReadOnlyList<string> Snapshot()
    {
        lock (Sync)
        {
            return Entries.ToList();
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            Entries.Clear();
        }
    }
}
