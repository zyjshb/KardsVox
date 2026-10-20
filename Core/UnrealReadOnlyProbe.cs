using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace KardsVox.Core;

internal sealed class UnrealReadOnlyProbe : IDisposable
{
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessQueryInformation = 0x0400;
    private readonly IntPtr process;
    private readonly long moduleBase;
    private readonly byte[] module;

    private static readonly byte[][] GObjectPatterns =
    [
        [0x4C,0x8B,0x0D,0,0,0,0,0x4D,0x85,0xC9],
        [0x4C,0x8B,0x15,0,0,0,0,0x4D,0x85,0xD2],
        [0x4C,0x8B,0x15,0,0,0,0,0x4C,0x8B,0xCA],
        [0x4C,0x8B,0x0D,0,0,0,0,0x49,0x8B,0x41],
        [0x4C,0x8B,0x0D,0,0,0,0,0x4D,0x8B,0x41],
        [0x48,0x8B,0x0D,0,0,0,0,0x48,0x85,0xC9,0x74],
        [0x4C,0x8B,0x05,0,0,0,0,0x4D,0x85,0xC0]
    ];

    private static readonly byte[][] GNamePatterns =
    [
        [0x48,0x8D,0x1D,0,0,0,0,0x48,0x89,0x1D,0,0,0,0],
        [0x48,0x89,0x1D,0,0,0,0,0x48,0x83,0xC4,0x28,0xC3],
        [0x48,0x8B,0x1D,0,0,0,0,0x48,0x85,0xDB,0x0F,0x85],
        [0x48,0x89,0x1D,0,0,0,0,0xE8,0,0,0,0],
        [0x48,0x8D,0x05,0,0,0,0,0x48,0x89,0x1D,0,0,0,0]
    ];

    public UnrealReadOnlyProbe()
    {
        var targets = Process.GetProcessesByName("kards-Win64-Shipping");
        if (targets.Length == 0) throw new InvalidOperationException("KARDS is not running.");
        using var target = targets.OrderByDescending(p => p.WorkingSet64).First();
        process = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, target.Id);
        if (process == IntPtr.Zero) throw new InvalidOperationException($"Could not open KARDS read-only (error {Marshal.GetLastWin32Error()}).");
        var main = target.MainModule ?? throw new InvalidOperationException("Could not inspect the KARDS main module.");
        moduleBase = main.BaseAddress.ToInt64();
        module = ReadModule(main.ModuleMemorySize);
    }

    public void DumpCandidates(TextWriter output)
    {
        output.WriteLine($"KARDS module: 0x{moduleBase:X}, {module.Length / 1024 / 1024:N0} MB");
        DumpPatternCandidates("GObjects", GObjectPatterns, output, TryDescribeObjects);
        DumpPatternCandidates("GNames", GNamePatterns, output, TryDescribeNames);
    }

    public void FindNamePool(TextWriter output)
    {
        byte[] needle = Encoding.ASCII.GetBytes("None");
        var entries = FindRemoteBytes(needle, 10000).ToArray();
        output.WriteLine($"'None' occurrences: {entries.Length}");
        foreach (long textAddress in entries)
        {
            long entry = textAddress - 2;
            ushort header = BinaryPrimitives.ReadUInt16LittleEndian(Read(entry, 2));
            int len1 = header >> 1, len6 = header >> 6;
            if (len1 != 4 && len6 != 4) continue;
            byte[] addressBytes = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(addressBytes, entry);
            int[] refs = FindAllExact(module, addressBytes).Take(16).ToArray();
            output.WriteLine($"FName entry candidate 0x{entry:X}, header=0x{header:X4}, module refs={refs.Length}");
            foreach (int reference in refs) output.WriteLine($"  blocks candidate: 0x{moduleBase + reference:X}");
        }
    }

    public void FindKardsObjects(TextWriter output)
    {
        long blocksBase = ResolveNameBlocks();
        output.WriteLine($"FName blocks: 0x{blocksBase:X}");
        string[] wanted = ["BP_Board_C", "BP_Board", "BP_BattleStateMonitor_C", "BP_BattleStateMonitor",
            "Default__BP_BattleStateMonitor_C", "BaseCard", "/Script/kards.BaseCardObject",
            "BP_HandCard_C", "BP_HandCard", "BP_BoardCard_C", "BP_BoardCard",
            "beingPlayFromHandCardIDs", "OnCardPlayedFromHandEvent", "soundEnterPlay", "PlayState", "MonitorPlayState", "IsMonitoring"];
        Dictionary<string,uint> ids = FindNameIds(blocksBase, wanted);
        foreach (string name in wanted)
            output.WriteLine(ids.TryGetValue(name, out uint id) ? $"FName {name} = 0x{id:X8}" : $"FName {name} not loaded");

        foreach (string objectName in wanted.Take(11))
        {
            if (!ids.TryGetValue(objectName, out uint id)) continue;
            byte[] needle = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(needle, id);
            int shown = 0;
            foreach (long nameAddress in FindRemoteBytes(needle, 20000))
            {
                long candidate = nameAddress - 0x18;
                long vtable = ReadInt64(candidate);
                long classObject = ReadInt64(candidate + 0x10);
                if (vtable < moduleBase || vtable >= moduleBase + module.Length || !IsUserPointer(classObject)) continue;
                string? className = ReadObjectName(classObject, blocksBase);
                if (className is null) continue;
                int number = BinaryPrimitives.ReadInt32LittleEndian(Read(candidate + 0x1C, 4));
                int internalIndex = BinaryPrimitives.ReadInt32LittleEndian(Read(candidate + 0x0C, 4));
                long outer = ReadInt64(candidate + 0x20);
                string outerName = IsUserPointer(outer) ? ReadObjectName(outer, blocksBase) ?? "?" : "null";
                output.WriteLine($"UObject {objectName}: 0x{candidate:X}, number={number}, index={internalIndex}, class={className}, outer={outerName}");
                if (++shown >= 32) break;
            }
        }
    }

    public void FindKardsFields(TextWriter output)
    {
        long blocksBase = ResolveNameBlocks();
        string[] wanted = ["beingPlayFromHandCardIDs", "beingPlayFromHandDelays", "beingPlayFromHandFromEnemy", "beingPlayFromHandTargetCardIDs", "OnCardPlayedFromHandEvent", "soundEnterPlay", "card_id", "faction", "Location", "baseCards", "CardData", "CardDataObject", "ownerCardID", "_baseCard", "selfBaseCardRef", "cardID", "cardID_6_15AE188B4A5596B13B0C13A85855EE24", "_cardType", "selectedCardType", "thisCardObject", "cardObject", "basecard_type", "type", "PlayState", "MonitorPlayState", "IsMonitoring"];
        Dictionary<string,uint> ids = FindNameIds(blocksBase, wanted);
        foreach (string fieldName in wanted)
        {
            if (!ids.TryGetValue(fieldName, out uint id)) continue;
            byte[] needle = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(needle, id);
            int shown = 0;
            foreach (long nameAddress in FindRemoteBytes(needle, 20000))
            foreach (int nameOffset in new[] { 0x20 })
            {
                long candidate = nameAddress - nameOffset;
                long vtable = ReadInt64(candidate);
                if (vtable < moduleBase || vtable >= moduleBase + module.Length) continue;
                byte[] data = Read(candidate, 0x80);
                long ownerTagged = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(0x10));
                long owner = ownerTagged & ~1L;
                string ownerName = IsUserPointer(owner) ? ReadObjectName(owner, blocksBase) ?? "?" : "?";
                output.WriteLine($"Field {fieldName}: base=0x{candidate:X}, name+0x{nameOffset:X}, " +
                    $"p8=0x{BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(8)):X}, " +
                    $"owner=0x{ownerTagged:X}({ownerName}), " +
                    $"p20=0x{BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(0x20)):X}, " +
                    $"i30={BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x30))}, i34={BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x34))}, " +
                    $"i40={BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x40))}, i44={BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x44))}, " +
                    $"i48={BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x48))}, i4C={BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x4C))}");
                output.WriteLine(Convert.ToHexString(data));
                if (++shown >= 1000) break;
            }
        }
    }

    public void SearchNames(string text, TextWriter output)
    {
        long blocksBase = ResolveNameBlocks();
        int matches = 0;
        for (int blockIndex = 0; blockIndex < 8192; blockIndex++)
        {
            long block = ReadInt64(blocksBase + blockIndex * 8L);
            if (!IsUserPointer(block)) break;
            int offset = 0;
            while (offset + 2 <= 65536)
            {
                ushort header = BinaryPrimitives.ReadUInt16LittleEndian(Read(block + offset, 2));
                int length = header >> 6;
                if (length == 0 || length > 1023) break;
                bool wide = (header & 1) != 0;
                int byteLength = wide ? length * 2 : length;
                byte[] bytes = Read(block + offset + 2, byteLength);
                string name = wide ? Encoding.Unicode.GetString(bytes) : Encoding.ASCII.GetString(bytes);
                if (name.Contains(text, StringComparison.OrdinalIgnoreCase))
                {
                    uint id = (uint)(blockIndex << 16 | offset / 2);
                    output.WriteLine($"0x{id:X8} {name}");
                    if (++matches >= 500) return;
                }
                offset += (2 + byteLength + 1) & ~1;
            }
        }
    }

    public void DumpClassProperties(string className, TextWriter output)
    {
        long blocksBase = ResolveNameBlocks();
        var ids = FindNameIds(blocksBase, [className]);
        if (!ids.TryGetValue(className, out uint id)) throw new InvalidOperationException($"FName not loaded: {className}");
        long klass = FindObjectsByNameId(id, blocksBase)
            .FirstOrDefault(obj => ReadObjectName(ReadInt64(obj + 0x10), blocksBase) is "BlueprintGeneratedClass" or "Class" or "ScriptStruct");
        if (klass == 0) throw new InvalidOperationException($"UClass not found: {className}");
        var classes = new List<long>();
        for (long current = klass; IsUserPointer(current) && classes.Count < 16 && !classes.Contains(current); current = ReadInt64(current + 0x40))
            classes.Add(current);
        foreach (long currentClass in classes)
        {
            output.WriteLine($"Class node: 0x{currentClass:X} name={ReadObjectName(currentClass, blocksBase)} super=0x{ReadInt64(currentClass + 0x40):X}");
            long field = ReadInt64(currentClass + 0x50);
            var seen = new HashSet<long>();
            for (int count = 0; count < 512 && IsUserPointer(field) && seen.Add(field); count++)
            {
                byte[] data = Read(field, 0x50);
                string? name = ReadFieldName(field, blocksBase);
                output.WriteLine($"  0x{field:X} {name} size={BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x34))} offset=0x{BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x44)):X} next=0x{BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(0x18)):X}");
                field = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(0x18));
            }
        }
    }

    public void DumpStructAddress(long address, TextWriter output)
    {
        long blocksBase = ResolveNameBlocks();
        var seenClasses = new HashSet<long>();
        for (long current = address; IsUserPointer(current) && seenClasses.Add(current); current = ReadInt64(current + 0x40))
        {
            output.WriteLine($"Struct node: 0x{current:X} name={ReadObjectName(current, blocksBase)} super=0x{ReadInt64(current + 0x40):X}");
            long field = ReadInt64(current + 0x50);
            var seen = new HashSet<long>();
            for (int count = 0; count < 1000 && IsUserPointer(field) && seen.Add(field); count++)
            {
                byte[] data = Read(field, 0x50);
                output.WriteLine($"  {ReadFieldName(field, blocksBase)} size={BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x34))} offset=0x{BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x44)):X}");
                field = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(0x18));
            }
        }
    }

    private string? ReadFieldName(long field, long blocksBase)
    {
        byte[] fname = Read(field + 0x20, 8);
        uint primary = unchecked((uint)BinaryPrimitives.ReadInt32LittleEndian(fname));
        uint secondary = unchecked((uint)BinaryPrimitives.ReadInt32LittleEndian(fname.AsSpan(4)));
        string? name = ReadName(primary, blocksBase);
        if (string.IsNullOrEmpty(name) || name == "None") name = ReadName(secondary, blocksBase);
        return name;
    }

    private string? ReadName(uint id, long blocksBase)
    {
        int blockIndex = (int)(id >> 16), offset = (int)(id & 0xffff) * 2;
        if (blockIndex >= 8192 || offset >= 65536) return null;
        long block = ReadInt64(blocksBase + blockIndex * 8L);
        if (!IsUserPointer(block)) return null;
        ushort header = BinaryPrimitives.ReadUInt16LittleEndian(Read(block + offset, 2));
        int length = header >> 6;
        if (length is <= 0 or > 256) return null;
        bool wide = (header & 1) != 0;
        byte[] bytes = Read(block + offset + 2, wide ? length * 2 : length);
        return wide ? Encoding.Unicode.GetString(bytes) : Encoding.ASCII.GetString(bytes);
    }

    public void FindObjectRegistry(TextWriter output)
    {
        long blocksBase = ResolveNameBlocks();
        Dictionary<string,uint> ids = FindNameIds(blocksBase, ["BP_Board_C", "BP_BattleStateMonitor_C"]);
        var known = new List<(string Name,long Address,int Index)>();
        foreach (var pair in ids)
        {
            foreach (long obj in FindObjectsByNameId(pair.Value, blocksBase))
            {
                string? className = ReadObjectName(ReadInt64(obj + 0x10), blocksBase);
                if (className != "BlueprintGeneratedClass") continue;
                int index = BinaryPrimitives.ReadInt32LittleEndian(Read(obj + 0x0C, 4));
                known.Add((pair.Key, obj, index));
                output.WriteLine($"Known {pair.Key}: object=0x{obj:X}, index={index}");
                break;
            }
        }

        var chunks = new Dictionary<int,HashSet<long>>();
        foreach (var item in known)
        {
            byte[] pointer = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(pointer, item.Address);
            int chunkIndex = item.Index / 65536, within = item.Index % 65536;
            foreach (long reference in FindRemoteBytes(pointer, 20000))
            {
                long chunk = reference - within * 24L;
                long alignment = chunk & 0xFFF;
                if (alignment is not (0 or 8) || ReadInt64(chunk + within * 24L) != item.Address) continue;
                long neighbor = ReadInt64(chunk + (within + 1L) * 24L);
                if (!IsUserPointer(neighbor)) continue;
                if (!chunks.TryGetValue(chunkIndex, out var set)) chunks[chunkIndex] = set = [];
                if (set.Add(chunk)) output.WriteLine($"  chunk[{chunkIndex}] candidate=0x{chunk:X} from item ref 0x{reference:X}");
            }
        }

        if (chunks.TryGetValue(0, out var zero) && chunks.TryGetValue(1, out var one))
        foreach (long chunk0 in zero)
        {
            byte[] pointer = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(pointer, chunk0);
            foreach (long table in FindRemoteBytes(pointer, 20000))
            {
                long chunk1 = ReadInt64(table + 8);
                if (one.Contains(chunk1))
                {
                    output.WriteLine($"FUObject chunks table: 0x{table:X}; chunk0=0x{chunk0:X}; chunk1=0x{chunk1:X}");
                    byte[] tableBytes = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(tableBytes, table);
                    foreach (long reference in FindRemoteBytes(tableBytes, 20000).Take(32))
                    {
                        byte[] around = Read(reference - 0x20, 0x60);
                        output.WriteLine($"  table ref=0x{reference:X}: {Convert.ToHexString(around)}");
                    }
                }
            }
        }
        else if (chunks.TryGetValue(0, out zero))
        foreach (long chunk0 in zero)
        {
            foreach (var item in known)
                output.WriteLine($"  flat check index {item.Index}: 0x{ReadInt64(chunk0 + item.Index * 24L):X} (expected 0x{item.Address:X})");
            byte[] pointer = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(pointer, chunk0);
            foreach (long table in FindRemoteBytes(pointer, 20000).Take(32))
                output.WriteLine($"  chunk table candidate=0x{table:X}; next=0x{ReadInt64(table + 8):X}");
        }
    }

    private IEnumerable<long> FindObjectsByNameId(uint id, long blocksBase)
    {
        byte[] needle = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(needle, id);
        foreach (long nameAddress in FindRemoteBytes(needle, 250000))
        {
            long candidate = nameAddress - 0x18;
            long vtable = ReadInt64(candidate), classObject = ReadInt64(candidate + 0x10);
            if (vtable < moduleBase || vtable >= moduleBase + module.Length || !IsUserPointer(classObject)) continue;
            if (ReadObjectName(classObject, blocksBase) is not null) yield return candidate;
        }
    }

    private long ResolveNameBlocks()
    {
        byte[] needle = Encoding.ASCII.GetBytes("None");
        foreach (long textAddress in FindRemoteBytes(needle, 10000))
        {
            long entry = textAddress - 2;
            ushort header = BinaryPrimitives.ReadUInt16LittleEndian(Read(entry, 2));
            if ((header >> 6) != 4) continue;
            byte[] addressBytes = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(addressBytes, entry);
            foreach (int reference in FindAllExact(module, addressBytes)) return moduleBase + reference;
        }
        throw new InvalidOperationException("UE5 FName pool was not found.");
    }

    private Dictionary<string,uint> FindNameIds(long blocksBase, IEnumerable<string> wanted)
    {
        var remaining = new HashSet<string>(wanted, StringComparer.Ordinal);
        var result = new Dictionary<string,uint>(StringComparer.Ordinal);
        for (int blockIndex = 0; blockIndex < 8192 && remaining.Count > 0; blockIndex++)
        {
            long block = ReadInt64(blocksBase + blockIndex * 8L);
            if (!IsUserPointer(block)) break;
            int offset = 0;
            while (offset + 2 <= 65536)
            {
                ushort header = BinaryPrimitives.ReadUInt16LittleEndian(Read(block + offset, 2));
                int length = header >> 6;
                if (length == 0 || length > 1023) break;
                bool wide = (header & 1) != 0;
                int byteLength = wide ? length * 2 : length;
                byte[] bytes = Read(block + offset + 2, byteLength);
                string name = wide ? Encoding.Unicode.GetString(bytes) : Encoding.ASCII.GetString(bytes);
                if (remaining.Remove(name))
                {
                    result[name] = (uint)(blockIndex << 16 | offset / 2);
                    if (remaining.Count == 0) break;
                }
                offset += (2 + byteLength + 1) & ~1;
            }
        }
        return result;
    }

    private string? ReadObjectName(long objectAddress, long blocksBase)
    {
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(Read(objectAddress + 0x18, 4));
        int blockIndex = (int)(id >> 16);
        int offset = (int)(id & 0xffff) * 2;
        if (blockIndex >= 8192 || offset >= 65536) return null;
        long block = ReadInt64(blocksBase + blockIndex * 8L);
        if (!IsUserPointer(block)) return null;
        ushort header = BinaryPrimitives.ReadUInt16LittleEndian(Read(block + offset, 2));
        int length = header >> 6;
        if (length is <= 0 or > 256) return null;
        bool wide = (header & 1) != 0;
        byte[] bytes = Read(block + offset + 2, wide ? length * 2 : length);
        return wide ? Encoding.Unicode.GetString(bytes) : Encoding.ASCII.GetString(bytes);
    }

    private void DumpPatternCandidates(string label, byte[][] patterns, TextWriter output, Func<long,string?> describe)
    {
        var seen = new HashSet<long>();
        foreach (byte[] pattern in patterns)
        {
            foreach (int match in FindAll(pattern))
            foreach (int extra in new[] { 3 })
            foreach (int addition in new[] { 4, -4 })
            {
                int at = match + extra;
                int relative = BinaryPrimitives.ReadInt32LittleEndian(module.AsSpan(at, 4));
                long target = moduleBase + at + relative + addition;
                if (!seen.Add(target)) continue;
                string? detail = describe(target);
                if (detail is not null) output.WriteLine($"{label}: 0x{target:X} {detail}");
            }
        }
    }

    private string? TryDescribeObjects(long address)
    {
        byte[] data = Read(address, 56);
        int max = BinaryPrimitives.ReadInt32LittleEndian(data);
        int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        long objects = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(8));
        if (count is > 5000 and < 2_000_000 && max >= count && IsUserPointer(objects))
            return $"flat array count={count:N0}, max={max:N0}, items=0x{objects:X}";

        // FChunkedFixedUObjectArray can sit at the target or after FUObjectArray bookkeeping.
        foreach (int offset in new[] { 0, 0x10 })
        {
            long chunks = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset));
            int chunkMax = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 16));
            int chunkCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 20));
            int numChunks = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 28));
            if (IsUserPointer(chunks) && chunkCount is > 5000 and < 2_000_000 && chunkMax >= chunkCount && numChunks is > 0 and < 1000)
                return $"chunked array +0x{offset:X}, count={chunkCount:N0}, chunks={numChunks}, table=0x{chunks:X}";
        }
        return null;
    }

    private string? TryDescribeNames(long address)
    {
        var bases = new List<long> { address };
        long pointer = ReadInt64(address);
        if (IsUserPointer(pointer)) bases.Add(pointer);
        foreach (long candidate in bases)
        foreach (int offset in new[] { 0, 0x10, 0x30 })
        {
            long blocks = candidate + offset;
            long first = ReadInt64(blocks);
            if (!IsUserPointer(first)) continue;
            byte[] entry = Read(first, 16);
            ushort header = BinaryPrimitives.ReadUInt16LittleEndian(entry);
            int length = header >> 1;
            if (length is <= 0 or > 64) continue;
            string name = (header & 1) == 0
                ? Encoding.ASCII.GetString(entry, 2, Math.Min(length, entry.Length - 2))
                : Encoding.Unicode.GetString(entry, 2, Math.Min(length * 2, entry.Length - 2));
            if (name.Equals("None", StringComparison.OrdinalIgnoreCase))
                return $"blocks=0x{blocks:X} (base 0x{candidate:X}+0x{offset:X}), entry0={name}";
        }
        return null;
    }

    private IEnumerable<int> FindAll(byte[] pattern)
    {
        for (int i = 0; i <= module.Length - pattern.Length; i++)
        {
            int j = 0;
            for (; j < pattern.Length; j++) if (pattern[j] != 0 && pattern[j] != module[i + j]) break;
            if (j == pattern.Length) yield return i;
        }
    }

    private IEnumerable<long> FindRemoteBytes(byte[] needle, int limit)
    {
        nuint address = 0;
        byte[] buffer = new byte[4 * 1024 * 1024 + needle.Length];
        int found = 0;
        while (found < limit && VirtualQueryEx(process, (IntPtr)address, out var info, (nuint)Marshal.SizeOf<MemoryBasicInformation>()) != 0)
        {
            nuint start = (nuint)info.BaseAddress, size = info.RegionSize;
            nuint next = start + size;
            if (next <= address) yield break;
            address = next;
            if (info.State != 0x1000 || (info.Protect & (0x100u | 0x01u)) != 0 ||
                (info.Protect & (0x02u|0x04u|0x08u|0x20u|0x40u|0x80u)) == 0) continue;

            nuint offset = 0;
            int carry = 0;
            while (offset < size && found < limit)
            {
                int wanted = (int)Math.Min((nuint)(4 * 1024 * 1024), size - offset);
                byte[] block = new byte[wanted];
                if (!ReadProcessMemory(process, (IntPtr)(start + offset), block, (nuint)wanted, out nuint read) || read == 0) break;
                Buffer.BlockCopy(block, 0, buffer, carry, (int)read);
                int count = carry + (int)read;
                int search = 0;
                while (search <= count - needle.Length)
                {
                    int hit = buffer.AsSpan(search, count - search).IndexOf(needle);
                    if (hit < 0) break;
                    int index = search + hit;
                    yield return (long)(start + offset) - carry + index;
                    if (++found >= limit) yield break;
                    search = index + 1;
                }
                carry = Math.Min(needle.Length - 1, count);
                buffer.AsSpan(count - carry, carry).CopyTo(buffer);
                offset += read;
            }
        }
    }

    private static IEnumerable<int> FindAllExact(byte[] haystack, byte[] needle)
    {
        int start = 0;
        while (start <= haystack.Length - needle.Length)
        {
            int hit = haystack.AsSpan(start).IndexOf(needle);
            if (hit < 0) yield break;
            int index = start + hit;
            yield return index;
            start = index + 1;
        }
    }

    private byte[] ReadModule(int size)
    {
        byte[] result = new byte[size];
        const int chunk = 64 * 1024;
        for (int offset = 0; offset < size; offset += chunk)
        {
            int wanted = Math.Min(chunk, size - offset);
            byte[] temp = new byte[wanted];
            if (ReadProcessMemory(process, (IntPtr)(moduleBase + offset), temp, (nuint)wanted, out nuint read) && read > 0)
                Buffer.BlockCopy(temp, 0, result, offset, (int)read);
        }
        return result;
    }

    private byte[] Read(long address, int size)
    {
        byte[] value = new byte[size];
        ReadProcessMemory(process, (IntPtr)address, value, (nuint)size, out _);
        return value;
    }

    private long ReadInt64(long address) => BinaryPrimitives.ReadInt64LittleEndian(Read(address, 8));
    private static bool IsUserPointer(long value) => value is > 0x10000 and < 0x0000800000000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    public void Dispose() => CloseHandle(process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, [Out] byte[] buffer, nuint size, out nuint bytesRead);
    [DllImport("kernel32.dll")]
    private static extern nuint VirtualQueryEx(IntPtr process, IntPtr address, out MemoryBasicInformation info, nuint length);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
