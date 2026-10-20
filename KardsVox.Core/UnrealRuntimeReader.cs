using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace KardsVox.Core;

internal sealed class UnrealRuntimeReader : IDisposable
{
	private struct MemoryBasicInformation
	{
		public nint BaseAddress;

		public nint AllocationBase;

		public uint AllocationProtect;

		public nuint RegionSize;

		public uint State;

		public uint Protect;

		public uint Type;
	}

	private const uint ProcessVmRead = 16u;

	private const uint ProcessQueryInformation = 1024u;

	private const int ObjectItemSize = 24;

	private const int ObjectsPerChunk = 65536;

	private readonly nint process;

	private readonly long moduleBase;

	private readonly int moduleSize;

	private readonly long nameBlocks;

	private readonly long objectArray;

	private readonly Dictionary<string, uint> nameIds;

	private readonly Dictionary<string, long> classObjects;
	private readonly RuntimeObjectIndex instanceIndex = new();
	private readonly Dictionary<int, string> resolvedNames = new();
	private DateTimeOffset nextRegistrySnapshot;
	public bool IncrementalInstances { get; set; }
	public bool IndexReady => instanceIndex.PendingCount == 0;
	public long ReadCalls { get; private set; }

	public int ProcessId { get; }

	public bool IsAlive
	{
		get
		{
			return GetExitCodeProcess(process, out uint code) && code == 259;
		}
	}

	public long BoardClass => classObjects["BP_Board_C"];

	public long BattleMonitorClass => classObjects["BP_BattleStateMonitor_C"];

	public int ObjectCount => ReadInt32(objectArray + 20);

	public long? GetBlueprintClass(string name)
	{
		if (!classObjects.TryGetValue(name, out var value))
		{
			return null;
		}
		return value;
	}

	public UnrealRuntimeReader()
	{
		Process[] processesByName = Process.GetProcessesByName("kards-Win64-Shipping");
		if (processesByName.Length == 0)
		{
			throw new InvalidOperationException("KARDS is not running.");
		}
		using Process target = processesByName.OrderByDescending((Process p) => p.WorkingSet64).First();
		ProcessId = target.Id;
		process = OpenProcess(1040u, inheritHandle: false, target.Id);
		if (process == IntPtr.Zero)
		{
			throw new InvalidOperationException($"Could not open KARDS read-only (error {Marshal.GetLastWin32Error()}).");
		}
		ProcessModule main = target.MainModule ?? throw new InvalidOperationException("Could not inspect the KARDS main module.");
		moduleBase = ((IntPtr)main.BaseAddress).ToInt64();
		moduleSize = main.ModuleMemorySize;
		nameBlocks = ResolveNameBlocks();
		string[] wanted = new string[10] { "BP_Board_C", "BP_BattleStateMonitor_C", "BP_BaseCard_C", "BP_HandCard_C", "BP_BoardCard_C", "beingPlayFromHandCardIDs", "beingPlayFromHandFromEnemy", "beingPlayFromHandTargetCardIDs", "PlayState", "IsMonitoring" };
		nameIds = FindNameIds(wanted);
		classObjects = new Dictionary<string, long>(StringComparer.Ordinal);
		foreach (string className in wanted.Take(5))
		{
			if (nameIds.TryGetValue(className, out var id))
			{
				long klass = FindObjectsByNameId(id).FirstOrDefault((long p) => ReadObjectName(ReadInt64(p + 16)) == "BlueprintGeneratedClass");
				if (klass != 0L)
				{
					classObjects[className] = klass;
				}
			}
		}
		if (!classObjects.ContainsKey("BP_Board_C") || !classObjects.ContainsKey("BP_BattleStateMonitor_C"))
		{
			throw new InvalidOperationException("KARDS runtime classes are not loaded yet.");
		}
		objectArray = ResolveObjectArray(classObjects["BP_Board_C"], classObjects["BP_BattleStateMonitor_C"]);
		GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
	}

	public IEnumerable<long> EnumerateObjects()
	{
		int count = ObjectCount;
		if ((count <= 0 || count > 2000000) ? true : false)
		{
			yield break;
		}
		long chunks = ReadInt64(objectArray);
		if (!IsUserPointer(chunks))
		{
			yield break;
		}
		int remaining = count;
		int chunkIndex = 0;
		while (remaining > 0)
		{
			long chunk = ReadInt64(chunks + (long)chunkIndex * 8L);
			int take = Math.Min(65536, remaining);
			if (!IsUserPointer(chunk))
			{
				break;
			}
			byte[] items = Read(chunk, take * 24);
			for (int i = 0; i < take; i++)
			{
				long obj = BinaryPrimitives.ReadInt64LittleEndian(items.AsSpan(i * 24, 8));
				if (IsUserPointer(obj))
				{
					yield return obj;
				}
			}
			remaining -= take;
			chunkIndex++;
		}
	}

	public IEnumerable<long> FindInstances(long classObject)
	{
		if (IncrementalInstances)
		{
			foreach (long obj in instanceIndex.Find(classObject))
				if (IsLiveInstance(obj, classObject)) yield return obj;
			yield break;
		}
		foreach (long obj in EnumerateObjects())
		{
			if (ReadInt64(obj + 16) == classObject)
			{
				yield return obj;
			}
		}
	}

	public void UpdateInstanceIndex()
	{
		if (DateTimeOffset.UtcNow >= nextRegistrySnapshot)
		{
			int count = ObjectCount;
			long chunks = ReadInt64(objectArray);
			if (count > 0 && count <= 2000000 && IsUserPointer(chunks))
			{
				// Newer objects are usually in the final chunk. Scan their identities first.
				for (int chunkIndex = (count - 1) / ObjectsPerChunk; chunkIndex >= 0; chunkIndex--)
				{
					int take = Math.Min(ObjectsPerChunk, count - chunkIndex * ObjectsPerChunk);
					long chunk = ReadInt64(chunks + chunkIndex * 8L);
					if (!IsUserPointer(chunk)) continue;
					byte[] items = new byte[take * ObjectItemSize];
					ReadCalls++;
					if (!ReadProcessMemory(process, (nint)chunk, items, (nuint)items.Length, out var received) || received != (nuint)items.Length)
						continue; // A partial read is not evidence that every object was removed.
					for (int i = take - 1; i >= 0; i--)
					{
						long address = BinaryPrimitives.ReadInt64LittleEndian(items.AsSpan(i * ObjectItemSize));
						int serial = BinaryPrimitives.ReadInt32LittleEndian(items.AsSpan(i * ObjectItemSize + 16));
						instanceIndex.Observe(chunkIndex * ObjectsPerChunk + i, IsUserPointer(address) ? address : 0, serial);
					}
				}
				instanceIndex.Trim(count);
			}
			nextRegistrySnapshot = DateTimeOffset.UtcNow.AddMilliseconds(200);
		}
		instanceIndex.Drain(address =>
		{
			long klass = ReadInt64(address + 16);
			return classObjects.Values.Contains(klass) ? klass : 0;
		}, 512, TimeSpan.FromMilliseconds(4));
	}

	public bool IsLiveInstance(long objectAddress, long classObject)
	{
		if (IsUserPointer(objectAddress))
		{
			return ReadInt64(objectAddress + 16) == classObject;
		}
		return false;
	}

	public (long Data, int Count, int Capacity) ReadArray(long owner, int offset)
	{
		byte[] value = Read(owner + offset, 16);
		return (Data: BinaryPrimitives.ReadInt64LittleEndian(value), Count: BinaryPrimitives.ReadInt32LittleEndian(value.AsSpan(8)), Capacity: BinaryPrimitives.ReadInt32LittleEndian(value.AsSpan(12)));
	}

	public int[] ReadIntArray(long owner, int offset, int max = 128)
	{
		(long, int, int) array = ReadArray(owner, offset);
		if (array.Item2 <= 0)
		{
			return Array.Empty<int>();
		}
		if (array.Item2 > max || array.Item3 < array.Item2 || !IsUserPointer(array.Item1))
		{
			return Array.Empty<int>();
		}
		byte[] bytes = Read(array.Item1, array.Item2 * 4);
		int[] values = new int[array.Item2];
		for (int i = 0; i < values.Length; i++)
		{
			values[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * 4));
		}
		return values;
	}

	public bool[] ReadBoolArray(long owner, int offset, int max = 128)
	{
		(long, int, int) array = ReadArray(owner, offset);
		if (array.Item2 <= 0)
		{
			return Array.Empty<bool>();
		}
		if (array.Item2 > max || array.Item3 < array.Item2 || !IsUserPointer(array.Item1))
		{
			return Array.Empty<bool>();
		}
		return (from b in Read(array.Item1, array.Item2)
			select b != 0).ToArray();
	}

	public string? GetObjectName(long obj)
	{
		if (!IsUserPointer(obj))
		{
			return null;
		}
		return ReadObjectName(obj);
	}

	public string? GetClassName(long obj)
	{
		long klass = ReadInt64(obj + 16);
		if (!IsUserPointer(klass))
		{
			return null;
		}
		return ReadObjectName(klass);
	}

	public int ReadInt(long address)
	{
		return ReadInt32(address);
	}

	public float ReadSingle(long address)
	{
		return BitConverter.ToSingle(Read(address, 4));
	}

	public long ReadPointer(long address)
	{
		return ReadInt64(address);
	}

	public byte ReadByte(long address)
	{
		return Read(address, 1)[0];
	}

	public byte[] ReadBlock(long address, int size)
	{
		return Read(address, size);
	}

	public bool IsPointer(long value)
	{
		return IsUserPointer(value);
	}

	private long ResolveObjectArray(long firstKnown, long secondKnown)
	{
		long[] obj = new long[2] { firstKnown, secondKnown };
		Dictionary<int, HashSet<long>> chunksByIndex = new Dictionary<int, HashSet<long>>();
		long[] array = obj;
		foreach (long obj2 in array)
		{
			int num = ReadInt32(obj2 + 12);
			int chunkIndex = num / 65536;
			int within = num % 65536;
			byte[] pointer = BitConverter.GetBytes(obj2);
			foreach (long item in FindRemoteBytes(pointer, 20000))
			{
				long chunk = item - within * 24;
				long num2 = chunk & 0xFFF;
				bool flag = ((num2 == 0L || num2 == 8) ? true : false);
				if (flag && ReadInt64(chunk + within * 24) == obj2)
				{
					if (!chunksByIndex.TryGetValue(chunkIndex, out var set))
					{
						set = (chunksByIndex[chunkIndex] = new HashSet<long>());
					}
					set.Add(chunk);
				}
			}
		}
		foreach (KeyValuePair<int, HashSet<long>> first in chunksByIndex)
		{
			foreach (long item2 in first.Value)
			{
				byte[] pointer2 = BitConverter.GetBytes(item2);
				foreach (long item3 in FindRemoteBytes(pointer2, 20000))
				{
					long table = item3 - (long)first.Key * 8L;
					bool allMatch = true;
					foreach (KeyValuePair<int, HashSet<long>> pair in chunksByIndex)
					{
						long candidate = ReadInt64(table + (long)pair.Key * 8L);
						if (!pair.Value.Contains(candidate))
						{
							allMatch = false;
							break;
						}
					}
					if (!allMatch)
					{
						continue;
					}
					byte[] tablePointer = BitConverter.GetBytes(table);
					foreach (int offset in FindModuleExact(tablePointer))
					{
						long candidate2 = moduleBase + offset;
						int num3 = ReadInt32(candidate2 + 16);
						int count = ReadInt32(candidate2 + 20);
						int maxChunks = ReadInt32(candidate2 + 24);
						int numChunks = ReadInt32(candidate2 + 28);
						if (num3 >= count && count > 10000 && maxChunks > 0 && maxChunks < 256 && numChunks > 0 && numChunks < 256)
						{
							return candidate2;
						}
					}
				}
			}
		}
		throw new InvalidOperationException("KARDS UObject registry was not found.");
	}

	private long ResolveNameBlocks()
	{
		byte[] needle = Encoding.ASCII.GetBytes("None");
		foreach (long item in FindRemoteBytes(needle, 10000))
		{
			long entry = item - 2;
			if (BinaryPrimitives.ReadUInt16LittleEndian(Read(entry, 2)) >> 6 != 4)
			{
				continue;
			}
			byte[] addressBytes = BitConverter.GetBytes(entry);
			using IEnumerator<int> enumerator2 = FindModuleExact(addressBytes).GetEnumerator();
			if (enumerator2.MoveNext())
			{
				int reference = enumerator2.Current;
				return moduleBase + reference;
			}
		}
		throw new InvalidOperationException("UE5 FName pool was not found.");
	}

	private Dictionary<string, uint> FindNameIds(IEnumerable<string> wanted)
	{
		HashSet<string> remaining = new HashSet<string>(wanted, StringComparer.Ordinal);
		Dictionary<string, uint> result = new Dictionary<string, uint>(StringComparer.Ordinal);
		for (int blockIndex = 0; blockIndex < 8192; blockIndex++)
		{
			if (remaining.Count <= 0)
			{
				break;
			}
			long block = ReadInt64(nameBlocks + (long)blockIndex * 8L);
			if (!IsUserPointer(block))
			{
				break;
			}
			byte[] nameBlock = Read(block, 131072);
			int bytesCount;
			for (int offset = 0; offset + 2 <= 131072; offset += (2 + bytesCount + 1) & -2)
			{
				ushort header = BinaryPrimitives.ReadUInt16LittleEndian(nameBlock.AsSpan(offset));
				int length = header >> 6;
				if (length == 0 || length > 1023)
				{
					break;
				}
				bool num = (header & 1) != 0;
				bytesCount = (num ? (length * 2) : length);
				if (offset + 2 + bytesCount > nameBlock.Length) break;
				ReadOnlySpan<byte> bytes = nameBlock.AsSpan(offset + 2, bytesCount);
				string name = (num ? Encoding.Unicode.GetString(bytes) : Encoding.ASCII.GetString(bytes));
				if (remaining.Remove(name))
				{
					result[name] = (uint)((blockIndex << 16) | (offset / 2));
				}
			}
		}
		return result;
	}

	private IEnumerable<long> FindObjectsByNameId(uint id)
	{
		byte[] needle = BitConverter.GetBytes(id);
		foreach (long item in FindRemoteBytes(needle, 20000))
		{
			long candidate = item - 24;
			long vtable = ReadInt64(candidate);
			long classObject = ReadInt64(candidate + 16);
			if (vtable >= moduleBase && vtable < moduleBase + moduleSize && IsUserPointer(classObject) && ReadObjectName(classObject) != null)
			{
				yield return candidate;
			}
		}
	}

	private string? ReadObjectName(long objectAddress)
	{
		int num = ReadInt32(objectAddress + 24);
		if (resolvedNames.TryGetValue(num, out var cached)) return cached;
		int blockIndex = num >>> 16;
		int offset = (num & 0xFFFF) * 2;
		if (blockIndex >= 8192 || offset >= 131072)
		{
			return null;
		}
		long block = ReadInt64(nameBlocks + (long)blockIndex * 8L);
		if (!IsUserPointer(block))
		{
			return null;
		}
		ushort header = BinaryPrimitives.ReadUInt16LittleEndian(Read(block + offset, 2));
		int length = header >> 6;
		if ((length <= 0 || length > 256) ? true : false)
		{
			return null;
		}
		bool wide = (header & 1) != 0;
		byte[] bytes = Read(block + offset + 2, wide ? (length * 2) : length);
		string name = wide ? Encoding.Unicode.GetString(bytes) : Encoding.ASCII.GetString(bytes);
		resolvedNames[num] = name;
		return name;
	}

	private IEnumerable<long> FindRemoteBytes(byte[] needle, int limit)
	{
		nuint address = 0u;
		byte[] buffer = new byte[4194304 + needle.Length];
		byte[] block = new byte[4194304];
		int found = 0;
		MemoryBasicInformation info;
		while (found < limit && VirtualQueryEx(process, (nint)address, out info, (nuint)Marshal.SizeOf<MemoryBasicInformation>()) != 0)
		{
			nuint start = (nuint)info.BaseAddress;
			nuint size = info.RegionSize;
			nuint next = start + size;
			if (next <= address)
			{
				break;
			}
			address = next;
			if (info.State != 4096 || (info.Protect & 0x101) != 0 || (info.Protect & 0xEE) == 0)
			{
				continue;
			}
			nuint offset = 0u;
			int carry = 0;
			nuint read;
			for (; offset < size; offset += read)
			{
				if (found >= limit)
				{
					break;
				}
				int wanted = (int)Math.Min(4194304u, size - offset);
				if (!ReadProcessMemory(process, (nint)(start + offset), block, (nuint)wanted, out read) || read == 0)
				{
					break;
				}
				Buffer.BlockCopy(block, 0, buffer, carry, (int)read);
				int count = carry + (int)read;
				int search = 0;
				while (search <= count - needle.Length)
				{
					int hit = buffer.AsSpan(search, count - search).IndexOf(needle);
					if (hit < 0)
					{
						break;
					}
					int index = search + hit;
					yield return (long)(start + offset) - (long)carry + index;
					int num = found + 1;
					found = num;
					if (num >= limit)
					{
						yield break;
					}
					search = index + 1;
				}
				carry = Math.Min(needle.Length - 1, count);
				buffer.AsSpan(count - carry, carry).CopyTo(buffer);
			}
		}
	}

	private IEnumerable<int> FindModuleExact(byte[] needle)
	{
		byte[] readBuffer = new byte[4194304];
		byte[] searchBuffer = new byte[4194304 + needle.Length];
		int carry = 0;
		int moduleOffset = 0;
		while (moduleOffset < moduleSize)
		{
			int wanted = Math.Min(4194304, moduleSize - moduleOffset);
			if (!ReadProcessMemory(process, (nint)(moduleBase + moduleOffset), readBuffer, (nuint)wanted, out var read) || read == 0)
			{
				moduleOffset += wanted;
				carry = 0;
				continue;
			}
			Buffer.BlockCopy(readBuffer, 0, searchBuffer, carry, (int)read);
			int count = carry + (int)read;
			int search = 0;
			while (search <= count - needle.Length)
			{
				int hit = searchBuffer.AsSpan(search, count - search).IndexOf(needle);
				if (hit < 0)
				{
					break;
				}
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
		ReadCalls++;
		byte[] value = new byte[size];
		ReadProcessMemory(process, (nint)address, value, (nuint)size, out var _);
		return value;
	}

	private int ReadInt32(long address)
	{
		return BinaryPrimitives.ReadInt32LittleEndian(Read(address, 4));
	}

	private long ReadInt64(long address)
	{
		return BinaryPrimitives.ReadInt64LittleEndian(Read(address, 8));
	}

	private static bool IsUserPointer(long value)
	{
		if (value > 65536)
		{
			return value < 140737488355328L;
		}
		return false;
	}

	public void Dispose()
	{
		CloseHandle(process);
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern nint OpenProcess(uint access, bool inheritHandle, int processId);

	[DllImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetExitCodeProcess(nint process, out uint exitCode);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool ReadProcessMemory(nint process, nint address, [Out] byte[] buffer, nuint size, out nuint bytesRead);

	[DllImport("kernel32.dll")]
	private static extern nuint VirtualQueryEx(nint process, nint address, out MemoryBasicInformation info, nuint length);

	[DllImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(nint handle);
}
