using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace KardsVox.Core;

// FUObjectItem identity includes its serial number: an address can be reused.
// Class reads are budgeted, while unchanged registry entries cost no remote reads.
internal sealed class RuntimeObjectIndex
{
    private sealed record Slot(long Address, int Serial, long Class = 0);
    private readonly Dictionary<int, Slot> slots = new();
    private readonly Queue<(int Index, Slot Value)> pending = new();
    private readonly Dictionary<long, HashSet<long>> classes = new();
    public int PendingCount => pending.Count;

    public void Observe(int index, long address, int serial)
    {
        if (slots.TryGetValue(index, out var old))
        {
            if (old.Address == address && old.Serial == serial) return;
            if (classes.TryGetValue(old.Class, out var members)) members.Remove(old.Address);
        }
        var slot = new Slot(address, serial);
        slots[index] = slot;
        if (address != 0) pending.Enqueue((index, slot));
    }

    public void Trim(int count)
    {
        foreach (int index in slots.Keys.Where(i => i >= count).ToArray())
        {
            Observe(index, 0, 0);
            slots.Remove(index);
        }
    }

    public int Drain(Func<long, long> readClass, int maximumReads, TimeSpan budget)
    {
        long start = Stopwatch.GetTimestamp();
        int reads = 0;
        while (pending.Count > 0 && reads < maximumReads && Stopwatch.GetElapsedTime(start) < budget)
        {
            var item = pending.Dequeue();
            if (!slots.TryGetValue(item.Index, out var current) || current != item.Value) continue;
            long klass = readClass(current.Address);
            reads++;
            slots[item.Index] = current with { Class = klass };
            if (klass == 0) continue;
            if (!classes.TryGetValue(klass, out var members)) classes[klass] = members = new();
            members.Add(current.Address);
        }
        return reads;
    }

    public long[] Find(long klass) => classes.TryGetValue(klass, out var members) ? members.ToArray() : [];

    public static void Verify()
    {
        var index = new RuntimeObjectIndex();
        for (int i = 0; i < 120000; i++) index.Observe(i, 0x10000 + i * 64, 1);
        int reads = 0;
        long ReadClass(long address) { reads++; return address % 128 == 0 ? 10 : 20; }
        if (index.Drain(ReadClass, 100, TimeSpan.FromSeconds(1)) != 100 || index.PendingCount != 119900)
            throw new InvalidOperationException("Registry discovery ignored its per-tick budget.");
        index.Drain(ReadClass, 120000, TimeSpan.FromSeconds(10));
        for (int i = 0; i < 120000; i++) index.Observe(i, 0x10000 + i * 64, 1);
        index.Drain(ReadClass, 120000, TimeSpan.FromSeconds(1));
        if (reads != 120000 || index.Find(10).Length != 60000)
            throw new InvalidOperationException("Unchanged objects were rescanned.");
        index.Observe(0, 0x10000, 2);
        index.Drain(_ => 30, 1, TimeSpan.FromSeconds(1));
        if (index.Find(10).Contains(0x10000) || !index.Find(30).Contains(0x10000))
            throw new InvalidOperationException("Reused UObject identity retained its old class.");
        index.Observe(1, 1234567, 2);
        index.Observe(1, 0, 3);
        index.Drain(_ => throw new InvalidOperationException("Read a removed object"), 10, TimeSpan.FromSeconds(1));
        index.Trim(0);
        if (index.Find(10).Length != 0 || index.Find(30).Length != 0)
            throw new InvalidOperationException("Removed registry entries remained live.");
        Console.WriteLine("Runtime index regression passed: 120000 objects, bounded work, zero unchanged class reads, serial reuse and removal.");
    }
}
