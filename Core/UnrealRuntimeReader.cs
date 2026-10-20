using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace KardsVox.Core;

/// <summary>
/// Minimal, read-only UE5 object reader for the current KARDS build.
/// It opens the game with PROCESS_QUERY_INFORMATION | PROCESS_VM_READ only.
/// </summary>
internal sealed class UnrealRuntimeReader : IDisposable
{
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessQueryInformation = 0x0400;
    private const int ObjectItemSize = 24;
    private const int ObjectsPerChunk = 65536;

    private readonly IntPtr process;
    private readonly long moduleBase;
    private readonly int moduleSize;
    private readonly long nameBlocks;
    private readonly long objectArray;
    private readonly Dictionary<string, uint> nameIds;
    private readonly Dictionary<string, long> classObjects;

    public int ProcessId { get; }
    public bool IsAlive
    {
        get
        {
            try { using var target = Process.GetProcessById(ProcessId); return !target.HasExited; }
            catch { return false; }
        }
    }
    public long BoardClass => classObjects["BP_Board_C"];
    public long BattleMonitorClass => classObjects["BP_BattleStateMonitor_C"];
    public long? GetBlueprintClass(string name) => classObjects.TryGetValue(name, out long value) ? value : null;

    public UnrealRuntimeReader()
    {
        var targets = Process.GetProcessesByName("kards-Win64-Shipping");
        if (targets.Length == 0) throw new InvalidOperationException("KARDS is not running.");
        using var target = targets.OrderByDescending(p => p.WorkingSet64).First();
        ProcessId = target.Id;
        process = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, target.Id);
        if (process == IntPtr.Zero)
            throw new InvalidOperationException($"Could not open KARDS read-only (error {Marshal.GetLastWin32Error()}).");
        var main = target.MainModule ?? throw new InvalidOperationException("Could not inspect the KARDS main module.");
        moduleBase = main.BaseAddress.ToInt64();
        moduleSize = main.ModuleMemorySize;
        nameBlocks = ResolveNameBlocks();

        string[] wanted =
        [
            "BP_Board_C", "BP_BattleStateMonitor_C", "BP_BaseCard_C", "BP_HandCard_C", "BP_BoardCard_C",
            "beingPlayFromHandCardIDs", "beingPlayFromHandFromEnemy",
            "beingPlayFromHandTargetCardIDs", "selfBaseCardRef", "PlayState", "IsMonitoring"
        ];
        nameIds = FindNameIds(wanted);
        classObjects = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (string className in wanted.Take(5))
        {
            if (!nameIds.TryGetValue(className, out uint id)) continue;
            long klass = FindObjectsByNameId(id)
                .FirstOrDefault(p => ReadObjectName(ReadInt64(p + 0x10)) == "BlueprintGeneratedClass");
            if (klass != 0) classObjects[className] = klass;
        }

        // Recent KARDS builds strip the generated Blueprint class FNames while keeping
        // their reflected properties. Resolve the owning UClass from a stable property
        // and offset so the reader survives that packaging change.
        if (!classObjects.ContainsKey("BP_Board_C") &&
            TryFindOwningClass("beingPlayFromHandCardIDs", 0x500, out long boardClass))
            classObjects["BP_Board_C"] = boardClass;

        if (!classObjects.ContainsKey("BP_BattleStateMonitor_C") &&
            TryFindOwningClass("IsMonitoring", 0x2D0, out long monitorClass))
            classObjects["BP_BattleStateMonitor_C"] = monitorClass;

        if (TryFindOwningClass("selfBaseCardRef", 0x808, out long visualCardClass))
        {
            classObjects.TryAdd("BP_BaseCard_C", visualCardClass);
            classObjects.TryAdd("BP_HandCard_C", visualCardClass);
            classObjects.TryAdd("BP_BoardCard_C", visualCardClass);
        }

        if (!classObjects.ContainsKey("BP_Board_C") || !classObjects.ContainsKey("BP_BattleStateMonitor_C"))
            throw new InvalidOperationException("KARDS battle objects are not loaded yet.");
        objectArray = ResolveObjectArray(classObjects["BP_Board_C"], classObjects["BP_BattleStateMonitor_C"]);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    public int ObjectCount => ReadInt32(objectArray + 0x14);

    public IEnumerable<long> EnumerateObjects()
        => EnumerateObjects(0);

    public IEnumerable<long> EnumerateObjects(int startIndex)
    {
        int count = ObjectCount;
        if (count is <= 0 or > 2_000_000) yield break;
        startIndex = Math.Clamp(startIndex, 0, count);
        long chunks = ReadInt64(objectArray);
        if (!IsUserPointer(chunks)) yield break;
        int firstChunk = startIndex / ObjectsPerChunk;
        int withinChunk = startIndex % ObjectsPerChunk;
        int remaining = count - startIndex;
        for (int chunkIndex = firstChunk; remaining > 0; chunkIndex++)
        {
            long chunk = ReadInt64(chunks + chunkIndex * 8L);
            int take = Math.Min(ObjectsPerChunk - withinChunk, remaining);
            if (!IsUserPointer(chunk)) yield break;
            byte[] items = Read(chunk + withinChunk * ObjectItemSize, take * ObjectItemSize);
            for (int i = 0; i < take; i++)
            {
                long obj = BinaryPrimitives.ReadInt64LittleEndian(items.AsSpan(i * ObjectItemSize, 8));
                if (IsUserPointer(obj)) yield return obj;
            }
            remaining -= take;
            withinChunk = 0;
        }
    }

    public IEnumerable<long> FindInstances(long classObject)
    {
        foreach (long obj in EnumerateObjects())
            if (IsClassOrSubclass(ReadInt64(obj + 0x10), classObject))
                yield return obj;
    }

    public IEnumerable<long> FindExactInstances(long classObject)
    {
        foreach (long obj in EnumerateObjects())
            if (ReadInt64(obj + 0x10) == classObject)
                yield return obj;
    }

    public IEnumerable<long> FindInstancesFromIndex(long classObject, int startIndex)
    {
        foreach (long obj in EnumerateObjects(startIndex))
            if (IsClassOrSubclass(ReadInt64(obj + 0x10), classObject))
                yield return obj;
    }

    public bool IsLiveInstance(long objectAddress, long classObject)
        => IsUserPointer(objectAddress) && IsClassOrSubclass(ReadInt64(objectAddress + 0x10), classObject);

    public (long Data, int Count, int Capacity) ReadArray(long owner, int offset)
    {
        byte[] value = Read(owner + offset, 16);
        return (BinaryPrimitives.ReadInt64LittleEndian(value),
            BinaryPrimitives.ReadInt32LittleEndian(value.AsSpan(8)),
            BinaryPrimitives.ReadInt32LittleEndian(value.AsSpan(12)));
    }

    public int[] ReadIntArray(long owner, int offset, int max = 128)
    {
        var array = ReadArray(owner, offset);
        if (array.Count <= 0) return [];
        if (array.Count > max || array.Capacity < array.Count || !IsUserPointer(array.Data)) return [];
        byte[] bytes = Read(array.Data, array.Count * 4);
        var values = new int[array.Count];
        for (int i = 0; i < values.Length; i++) values[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * 4));
        return values;
    }

    public bool[] ReadBoolArray(long owner, int offset, int max = 128)
    {
        var array = ReadArray(owner, offset);
        if (array.Count <= 0) return [];
        if (array.Count > max || array.Capacity < array.Count || !IsUserPointer(array.Data)) return [];
        byte[] bytes = Read(array.Data, array.Count);
        return bytes.Select(b => b != 0).ToArray();
    }

    public string? GetObjectName(long obj) => IsUserPointer(obj) ? ReadObjectName(obj) : null;
    public long GetClassAddress(long obj) => IsUserPointer(obj) ? ReadInt64(obj + 0x10) : 0;
    public long GetSuperClass(long klass) => IsUserPointer(klass) ? ReadInt64(klass + 0x40) : 0;
    public string? GetClassName(long obj)
    {
        long klass = ReadInt64(obj + 0x10);
        return IsUserPointer(klass) ? ReadObjectName(klass) : null;
    }
    public int ReadInt(long address) => ReadInt32(address);
    public float ReadSingle(long address) => BitConverter.ToSingle(Read(address, 4));
    public long ReadPointer(long address) => ReadInt64(address);
    public byte ReadByte(long address) => Read(address, 1)[0];
    public byte[] ReadBlock(long address, int size) => Read(address, size);
    public bool IsPointer(long value) => IsUserPointer(value);

    private long ResolveObjectArray(long firstKnown, long secondKnown)
    {
        var known = new[] { firstKnown, secondKnown };
        var chunksByIndex = new Dictionary<int, HashSet<long>>();
        foreach (long obj in known)
        {
            int index = ReadInt32(obj + 0x0C);
            int chunkIndex = index / ObjectsPerChunk;
            int within = index % ObjectsPerChunk;
            byte[] pointer = BitConverter.GetBytes(obj);
            foreach (long reference in FindRemoteBytes(pointer, 20000))
            {
                long chunk = reference - within * ObjectItemSize;
                if ((chunk & 0xFFF) is not (0 or 8)) continue;
                if (ReadInt64(chunk + within * ObjectItemSize) != obj) continue;
                if (!chunksByIndex.TryGetValue(chunkIndex, out var set)) chunksByIndex[chunkIndex] = set = [];
                set.Add(chunk);
            }
        }

        foreach (var first in chunksByIndex)
        foreach (long firstChunk in first.Value)
        {
            byte[] pointer = BitConverter.GetBytes(firstChunk);
            foreach (long tableEntry in FindRemoteBytes(pointer, 20000))
            {
                long table = tableEntry - first.Key * 8L;
                bool allMatch = true;
                foreach (var pair in chunksByIndex)
                {
                    long candidate = ReadInt64(table + pair.Key * 8L);
                    if (!pair.Value.Contains(candidate)) { allMatch = false; break; }
                }
                if (!allMatch) continue;

                byte[] tablePointer = BitConverter.GetBytes(table);
                foreach (int offset in FindModuleExact(tablePointer))
                {
                    long candidate = moduleBase + offset;
                    int max = ReadInt32(candidate + 0x10);
                    int count = ReadInt32(candidate + 0x14);
                    int maxChunks = ReadInt32(candidate + 0x18);
                    int numChunks = ReadInt32(candidate + 0x1C);
                    if (max >= count && count > 10000 && maxChunks is > 0 and < 256 && numChunks is > 0 and < 256)
                        return candidate;
                }
            }
        }
        throw new InvalidOperationException("KARDS UObject registry was not found.");
    }

    private long ResolveNameBlocks()
    {
        byte[] needle = Encoding.ASCII.GetBytes("None");
        foreach (long textAddress in FindRemoteBytes(needle, 10000))
        {
            long entry = textAddress - 2;
            ushort header = BinaryPrimitives.ReadUInt16LittleEndian(Read(entry, 2));
            if ((header >> 6) != 4) continue;
            byte[] addressBytes = BitConverter.GetBytes(entry);
            foreach (int reference in FindModuleExact(addressBytes)) return moduleBase + reference;
        }
        throw new InvalidOperationException("UE5 FName pool was not found.");
    }

    private Dictionary<string, uint> FindNameIds(IEnumerable<string> wanted)
    {
        var remaining = new HashSet<string>(wanted, StringComparer.Ordinal);
        var result = new Dictionary<string, uint>(StringComparer.Ordinal);
        for (int blockIndex = 0; blockIndex < 8192 && remaining.Count > 0; blockIndex++)
        {
            long block = ReadInt64(nameBlocks + blockIndex * 8L);
            if (!IsUserPointer(block)) break;
            int offset = 0;
            while (offset + 2 <= 65536)
            {
                ushort header = BinaryPrimitives.ReadUInt16LittleEndian(Read(block + offset, 2));
                int length = header >> 6;
                if (length == 0 || length > 1023) break;
                bool wide = (header & 1) != 0;
                int bytesCount = wide ? length * 2 : length;
                byte[] bytes = Read(block + offset + 2, bytesCount);
                string name = wide ? Encoding.Unicode.GetString(bytes) : Encoding.ASCII.GetString(bytes);
                if (remaining.Remove(name)) result[name] = (uint)(blockIndex << 16 | offset / 2);
                offset += (2 + bytesCount + 1) & ~1;
            }
        }
        return result;
    }

    private IEnumerable<long> FindObjectsByNameId(uint id)
    {
        byte[] needle = BitConverter.GetBytes(id);
        foreach (long nameAddress in FindRemoteBytes(needle, 20000))
        {
            long candidate = nameAddress - 0x18;
            long vtable = ReadInt64(candidate), classObject = ReadInt64(candidate + 0x10);
            if (vtable < moduleBase || vtable >= moduleBase + moduleSize || !IsUserPointer(classObject)) continue;
            if (ReadObjectName(classObject) is not null) yield return candidate;
        }
    }

    private bool TryFindOwningClass(string propertyName, int expectedOffset, out long ownerClass)
    {
        ownerClass = 0;
        if (!nameIds.TryGetValue(propertyName, out uint id)) return false;

        byte[] needle = BitConverter.GetBytes(id);
        foreach (long nameAddress in FindRemoteBytes(needle, 250000))
        {
            // FField: vtable + flags + tagged owner + next + FName. FProperty adds
            // ElementSize at +0x34 and Offset_Internal at +0x44 in this UE5 build.
            long property = nameAddress - 0x20;
            long vtable = ReadInt64(property);
            if (vtable < moduleBase || vtable >= moduleBase + moduleSize) continue;
            int elementSize = ReadInt32(property + 0x34);
            int offset = ReadInt32(property + 0x44);
            if (offset != expectedOffset || elementSize is <= 0 or > 0x10000) continue;

            long taggedOwner = ReadInt64(property + 0x10);
            long owner = taggedOwner & ~1L;
            if (!IsUserPointer(owner)) continue;
            long ownerVtable = ReadInt64(owner);
            long ownerClassObject = ReadInt64(owner + 0x10);
            if (ownerVtable < moduleBase || ownerVtable >= moduleBase + moduleSize || !IsUserPointer(ownerClassObject))
                continue;

            ownerClass = owner;
            return true;
        }
        return false;
    }

    private bool IsClassOrSubclass(long candidateClass, long expectedClass)
    {
        for (int depth = 0; depth < 64 && IsUserPointer(candidateClass); depth++)
        {
            if (candidateClass == expectedClass) return true;
            long parent = ReadInt64(candidateClass + 0x40);
            if (parent == candidateClass) break;
            candidateClass = parent;
        }
        return false;
    }

    private string? ReadObjectName(long objectAddress)
    {
        uint id = unchecked((uint)ReadInt32(objectAddress + 0x18));
        int blockIndex = (int)(id >> 16), offset = (int)(id & 0xffff) * 2;
        if (blockIndex >= 8192 || offset >= 65536) return null;
        long block = ReadInt64(nameBlocks + blockIndex * 8L);
        if (!IsUserPointer(block)) return null;
        ushort header = BinaryPrimitives.ReadUInt16LittleEndian(Read(block + offset, 2));
        int length = header >> 6;
        if (length is <= 0 or > 256) return null;
        bool wide = (header & 1) != 0;
        byte[] bytes = Read(block + offset + 2, wide ? length * 2 : length);
        return wide ? Encoding.Unicode.GetString(bytes) : Encoding.ASCII.GetString(bytes);
    }

    private IEnumerable<long> FindRemoteBytes(byte[] needle, int limit)
    {
        nuint address = 0;
        byte[] buffer = new byte[4 * 1024 * 1024 + needle.Length];
        byte[] block = new byte[4 * 1024 * 1024];
        int found = 0;
        while (found < limit && VirtualQueryEx(process, (IntPtr)address, out var info, (nuint)Marshal.SizeOf<MemoryBasicInformation>()) != 0)
        {
            nuint start = (nuint)info.BaseAddress, size = info.RegionSize, next = start + size;
            if (next <= address) yield break;
            address = next;
            if (info.State != 0x1000 || (info.Protect & (0x100u | 0x01u)) != 0 ||
                (info.Protect & (0x02u | 0x04u | 0x08u | 0x20u | 0x40u | 0x80u)) == 0) continue;
            nuint offset = 0;
            int carry = 0;
            while (offset < size && found < limit)
            {
                int wanted = (int)Math.Min((nuint)(4 * 1024 * 1024), size - offset);
                if (!ReadProcessMemory(process, (IntPtr)(start + offset), block, (nuint)wanted, out nuint read) || read == 0) break;
                Buffer.BlockCopy(block, 0, buffer, carry, (int)read);
                int count = carry + (int)read, search = 0;
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

    private IEnumerable<int> FindModuleExact(byte[] needle)
    {
        const int chunkSize = 4 * 1024 * 1024;
        byte[] readBuffer = new byte[chunkSize];
        byte[] searchBuffer = new byte[chunkSize + needle.Length];
        int carry = 0;
        for (int moduleOffset = 0; moduleOffset < moduleSize;)
        {
            int wanted = Math.Min(chunkSize, moduleSize - moduleOffset);
            if (!ReadProcessMemory(process, (IntPtr)(moduleBase + moduleOffset), readBuffer, (nuint)wanted, out nuint read) || read == 0)
            {
                moduleOffset += wanted;
                carry = 0;
                continue;
            }
            Buffer.BlockCopy(readBuffer, 0, searchBuffer, carry, (int)read);
            int count = carry + (int)read, search = 0;
            while (search <= count - needle.Length)
            {
                int hit = searchBuffer.AsSpan(search, count - search).IndexOf(needle);
                if (hit < 0) break;
                int index = search + hit;
                yield return moduleOffset - carry + index;
                search = index + 1;
            }
            carry = Math.Min(needle.Length - 1, count);
            searchBuffer.AsSpan(count - carry, carry).CopyTo(searchBuffer);
            moduleOffset += (int)read;
        }
    }

    private byte[] Read(long address, int size)
    {
        byte[] value = new byte[size];
        ReadProcessMemory(process, (IntPtr)address, value, (nuint)size, out _);
        return value;
    }
    private int ReadInt32(long address) => BinaryPrimitives.ReadInt32LittleEndian(Read(address, 4));
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
