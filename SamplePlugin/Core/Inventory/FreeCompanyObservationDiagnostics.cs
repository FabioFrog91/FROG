using System;
using System.Collections.Generic;
using System.Linq;

namespace FROG.Core.Inventory;

public sealed class FreeCompanyObservationDiagnostics
{
    private const int MaxEntries = 500;

    private readonly object sync = new();
    private readonly Queue<string> entries = new();

    public void Add(string message)
    {
        var line =
            $"{DateTime.Now:HH:mm:ss.fff} | {message}";

        lock (sync)
        {
            entries.Enqueue(line);

            while (entries.Count > MaxEntries)
            {
                entries.Dequeue();
            }
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (sync)
        {
            return entries.ToList();
        }
    }

    public void Clear()
    {
        lock (sync)
        {
            entries.Clear();
        }
    }
}
