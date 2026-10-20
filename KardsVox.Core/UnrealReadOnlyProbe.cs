using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace KardsVox.Core;

internal sealed class UnrealReadOnlyProbe : IDisposable
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

	private readonly nint process;

	private readonly long moduleBase;

	private readonly byte[] module;

	private static readonly byte[][] GObjectPatterns = new byte[7][]
	{
		new byte[10] { 76, 139, 13, 0, 0, 0, 0, 77, 133, 201 },
		new byte[10] { 76, 139, 21, 0, 0, 0, 0, 77, 133, 210 },
		new byte[10] { 76, 139, 21, 0, 0, 0, 0, 76, 139, 202 },
		new byte[10] { 76, 139, 13, 0, 0, 0, 0, 73, 139, 65 },
		new byte[10] { 76, 139, 13, 0, 0, 0, 0, 77, 139, 65 },
		new byte[11]
		{
			72, 139, 13, 0, 0, 0, 0, 72, 133, 201,
			116
		},
		new byte[10] { 76, 139, 5, 0, 0, 0, 0, 77, 133, 192 }
	};

	private static readonly byte[][] GNamePatterns = new byte[5][]
	{
		new byte[14]
		{
			72, 141, 29, 0, 0, 0, 0, 72, 137, 29,
			0, 0, 0, 0
		},
		new byte[12]
		{
			72, 137, 29, 0, 0, 0, 0, 72, 131, 196,
			40, 195
		},
		new byte[12]
		{
			72, 139, 29, 0, 0, 0, 0, 72, 133, 219,
			15, 133
		},
		new byte[12]
		{
			72, 137, 29, 0, 0, 0, 0, 232, 0, 0,
			0, 0
		},
		new byte[14]
		{
			72, 141, 5, 0, 0, 0, 0, 72, 137, 29,
			0, 0, 0, 0
		}
	};

	public UnrealReadOnlyProbe()
	{
		Process[] processesByName = Process.GetProcessesByName("kards-Win64-Shipping");
		if (processesByName.Length == 0)
		{
			throw new InvalidOperationException("KARDS is not running.");
		}
		using Process target = processesByName.OrderByDescending((Process p) => p.WorkingSet64).First();
		process = OpenProcess(1040u, inheritHandle: false, target.Id);
		if (process == IntPtr.Zero)
		{
			throw new InvalidOperationException($"Could not open KARDS read-only (error {Marshal.GetLastWin32Error()}).");
		}
		ProcessModule main = target.MainModule ?? throw new InvalidOperationException("Could not inspect the KARDS main module.");
		moduleBase = ((IntPtr)main.BaseAddress).ToInt64();
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
		long[] entries = FindRemoteBytes(needle, 10000).ToArray();
		output.WriteLine($"'None' occurrences: {entries.Length}");
		long[] array = entries;
		for (int i = 0; i < array.Length; i++)
		{
			long entry = array[i] - 2;
			ushort header = BinaryPrimitives.ReadUInt16LittleEndian(Read(entry, 2));
			int num = header >> 1;
			int len6 = header >> 6;
			if (num == 4 || len6 == 4)
			{
				byte[] addressBytes = new byte[8];
				BinaryPrimitives.WriteInt64LittleEndian(addressBytes, entry);
				int[] refs = FindAllExact(module, addressBytes).Take(16).ToArray();
				output.WriteLine($"FName entry candidate 0x{entry:X}, header=0x{header:X4}, module refs={refs.Length}");
				int[] array2 = refs;
				foreach (int reference in array2)
				{
					output.WriteLine($"  blocks candidate: 0x{moduleBase + reference:X}");
				}
			}
		}
	}

	public void FindKardsObjects(TextWriter output)
	{
		long blocksBase = ResolveNameBlocks();
		output.WriteLine($"FName blocks: 0x{blocksBase:X}");
		string[] wanted = new string[12]
		{
			"BP_Board_C", "BP_BattleStateMonitor_C", "BaseCard", "/Script/kards.BaseCardObject", "BP_HandCard_C", "BP_BoardCard_C", "beingPlayFromHandCardIDs", "OnCardPlayedFromHandEvent", "soundEnterPlay", "PlayState",
			"MonitorPlayState", "IsMonitoring"
		};
		Dictionary<string, uint> ids = FindNameIds(blocksBase, wanted);
		string[] array = wanted;
		foreach (string name in array)
		{
			output.WriteLine(ids.TryGetValue(name, out var id) ? $"FName {name} = 0x{id:X8}" : ("FName " + name + " not loaded"));
		}
		foreach (string objectName in wanted.Take(6))
		{
			if (!ids.TryGetValue(objectName, out var id2))
			{
				continue;
			}
			byte[] needle = new byte[4];
			BinaryPrimitives.WriteUInt32LittleEndian(needle, id2);
			int shown = 0;
			foreach (long item in FindRemoteBytes(needle, 20000))
			{
				long candidate = item - 24;
				long vtable = ReadInt64(candidate);
				long classObject = ReadInt64(candidate + 16);
				if (vtable < moduleBase || vtable >= moduleBase + module.Length || !IsUserPointer(classObject))
				{
					continue;
				}
				string className = ReadObjectName(classObject, blocksBase);
				if (className != null)
				{
					int number = BinaryPrimitives.ReadInt32LittleEndian(Read(candidate + 28, 4));
					int internalIndex = BinaryPrimitives.ReadInt32LittleEndian(Read(candidate + 12, 4));
					long outer = ReadInt64(candidate + 32);
					string outerName = (IsUserPointer(outer) ? (ReadObjectName(outer, blocksBase) ?? "?") : "null");
					output.WriteLine($"UObject {objectName}: 0x{candidate:X}, number={number}, index={internalIndex}, class={className}, outer={outerName}");
					if (++shown >= 32)
					{
						break;
					}
				}
			}
		}
	}

	public void FindKardsFields(TextWriter output)
	{
		long blocksBase = ResolveNameBlocks();
		string[] wanted = new string[26]
		{
			"beingPlayFromHandCardIDs", "beingPlayFromHandDelays", "beingPlayFromHandFromEnemy", "beingPlayFromHandTargetCardIDs", "OnCardPlayedFromHandEvent", "soundEnterPlay", "card_id", "faction", "Location", "baseCards",
			"CardData", "CardDataObject", "ownerCardID", "_baseCard", "selfBaseCardRef", "cardID", "cardID_6_15AE188B4A5596B13B0C13A85855EE24", "_cardType", "selectedCardType", "thisCardObject",
			"cardObject", "basecard_type", "type", "PlayState", "MonitorPlayState", "IsMonitoring"
		};
		Dictionary<string, uint> ids = FindNameIds(blocksBase, wanted);
		string[] array = wanted;
		foreach (string fieldName in array)
		{
			if (!ids.TryGetValue(fieldName, out var id))
			{
				continue;
			}
			byte[] needle = new byte[4];
			BinaryPrimitives.WriteUInt32LittleEndian(needle, id);
			int shown = 0;
			foreach (long nameAddress in FindRemoteBytes(needle, 20000))
			{
				int[] array2 = new int[1] { 32 };
				foreach (int nameOffset in array2)
				{
					long candidate = nameAddress - nameOffset;
					long vtable = ReadInt64(candidate);
					if (vtable >= moduleBase && vtable < moduleBase + module.Length)
					{
						byte[] data = Read(candidate, 128);
						long ownerTagged = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(16));
						long owner = ownerTagged & -2;
						string ownerName = (IsUserPointer(owner) ? (ReadObjectName(owner, blocksBase) ?? "?") : "?");
						output.WriteLine($"Field {fieldName}: base=0x{candidate:X}, name+0x{nameOffset:X}, p8=0x{BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(8)):X}, owner=0x{ownerTagged:X}({ownerName}), p20=0x{BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(32)):X}, i30={BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(48))}, i34={BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(52))}, i40={BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(64))}, i44={BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(68))}, i48={BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(72))}, i4C={BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(76))}");
						output.WriteLine(Convert.ToHexString(data));
						if (++shown >= 1000)
						{
							break;
						}
					}
				}
			}
		}
	}

	public void SearchNames(string text, TextWriter output)
	{
		long blocksBase = ResolveNameBlocks();
		int matches = 0;
		for (int blockIndex = 0; blockIndex < 8192; blockIndex++)
		{
			long block = ReadInt64(blocksBase + (long)blockIndex * 8L);
			if (!IsUserPointer(block))
			{
				break;
			}
			int byteLength;
			for (int offset = 0; offset + 2 <= 131072; offset += (2 + byteLength + 1) & -2)
			{
				ushort header = BinaryPrimitives.ReadUInt16LittleEndian(Read(block + offset, 2));
				int length = header >> 6;
				if (length == 0 || length > 1023)
				{
					break;
				}
				bool num = (header & 1) != 0;
				byteLength = (num ? (length * 2) : length);
				byte[] bytes = Read(block + offset + 2, byteLength);
				string name = (num ? Encoding.Unicode.GetString(bytes) : Encoding.ASCII.GetString(bytes));
				if (name.Contains(text, StringComparison.OrdinalIgnoreCase))
				{
					uint id = (uint)((blockIndex << 16) | (offset / 2));
					output.WriteLine($"0x{id:X8} {name}");
					if (++matches >= 500)
					{
						return;
					}
				}
			}
		}
	}

	public void DumpClassProperties(string className, TextWriter output)
	{
		long blocksBase = ResolveNameBlocks();
		if (!FindNameIds(blocksBase, new _003C_003Ez__ReadOnlySingleElementList<string>(className)).TryGetValue(className, out var id))
		{
			throw new InvalidOperationException("FName not loaded: " + className);
		}
		long num = FindObjectsByNameId(id, blocksBase).FirstOrDefault(delegate(long obj)
		{
			switch (ReadObjectName(ReadInt64(obj + 16), blocksBase))
			{
			case "BlueprintGeneratedClass":
			case "Class":
			case "ScriptStruct":
				return true;
			default:
				return false;
			}
		});
		if (num == 0L)
		{
			throw new InvalidOperationException("UClass not found: " + className);
		}
		List<long> classes = new List<long>();
		long current = num;
		while (IsUserPointer(current) && classes.Count < 16 && !classes.Contains(current))
		{
			classes.Add(current);
			current = ReadInt64(current + 64);
		}
		foreach (long currentClass in classes)
		{
			output.WriteLine($"Class node: 0x{currentClass:X} name={ReadObjectName(currentClass, blocksBase)} super=0x{ReadInt64(currentClass + 64):X}");
			long field = ReadInt64(currentClass + 80);
			HashSet<long> seen = new HashSet<long>();
			for (int count = 0; count < 512; count++)
			{
				if (!IsUserPointer(field))
				{
					break;
				}
				if (!seen.Add(field))
				{
					break;
				}
				byte[] data = Read(field, 80);
				string name = ReadFieldName(field, blocksBase);
				output.WriteLine($"  0x{field:X} {name} size={BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(52))} offset=0x{BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(68)):X} next=0x{BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(24)):X}");
				field = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(24));
			}
		}
	}

	public void DumpStructAddress(long address, TextWriter output)
	{
		long blocksBase = ResolveNameBlocks();
		HashSet<long> seenClasses = new HashSet<long>();
		long current = address;
		while (IsUserPointer(current) && seenClasses.Add(current))
		{
			output.WriteLine($"Struct node: 0x{current:X} name={ReadObjectName(current, blocksBase)} super=0x{ReadInt64(current + 64):X}");
			long field = ReadInt64(current + 80);
			HashSet<long> seen = new HashSet<long>();
			for (int count = 0; count < 1000; count++)
			{
				if (!IsUserPointer(field))
				{
					break;
				}
				if (!seen.Add(field))
				{
					break;
				}
				byte[] data = Read(field, 80);
				output.WriteLine($"  {ReadFieldName(field, blocksBase)} size={BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(52))} offset=0x{BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(68)):X}");
				field = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(24));
			}
			current = ReadInt64(current + 64);
		}
	}

	private string? ReadFieldName(long field, long blocksBase)
	{
		byte[] array = Read(field + 32, 8);
		uint primary = (uint)BinaryPrimitives.ReadInt32LittleEndian(array);
		uint secondary = (uint)BinaryPrimitives.ReadInt32LittleEndian(array.AsSpan(4));
		string name = ReadName(primary, blocksBase);
		if (string.IsNullOrEmpty(name) || name == "None")
		{
			name = ReadName(secondary, blocksBase);
		}
		return name;
	}

	private string? ReadName(uint id, long blocksBase)
	{
		int blockIndex = (int)(id >> 16);
		int offset = (int)((id & 0xFFFF) * 2);
		if (blockIndex >= 8192 || offset >= 131072)
		{
			return null;
		}
		long block = ReadInt64(blocksBase + (long)blockIndex * 8L);
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
		if (!wide)
		{
			return Encoding.ASCII.GetString(bytes);
		}
		return Encoding.Unicode.GetString(bytes);
	}

	public void FindObjectRegistry(TextWriter output)
	{
		long blocksBase = ResolveNameBlocks();
		Dictionary<string, uint> dictionary = FindNameIds(blocksBase, new _003C_003Ez__ReadOnlyArray<string>(new string[2] { "BP_Board_C", "BP_BattleStateMonitor_C" }));
		List<(string, long, int)> known = new List<(string, long, int)>();
		foreach (KeyValuePair<string, uint> pair in dictionary)
		{
			foreach (long obj in FindObjectsByNameId(pair.Value, blocksBase))
			{
				if (!(ReadObjectName(ReadInt64(obj + 16), blocksBase) != "BlueprintGeneratedClass"))
				{
					int index = BinaryPrimitives.ReadInt32LittleEndian(Read(obj + 12, 4));
					known.Add((pair.Key, obj, index));
					output.WriteLine($"Known {pair.Key}: object=0x{obj:X}, index={index}");
					break;
				}
			}
		}
		Dictionary<int, HashSet<long>> chunks = new Dictionary<int, HashSet<long>>();
		foreach (var item in known)
		{
			byte[] pointer = new byte[8];
			BinaryPrimitives.WriteInt64LittleEndian(pointer, item.Item2);
			int chunkIndex = item.Item3 / 65536;
			int within = item.Item3 % 65536;
			foreach (long reference in FindRemoteBytes(pointer, 20000))
			{
				long chunk = reference - (long)within * 24L;
				long alignment = chunk & 0xFFF;
				bool flag = ((alignment == 0L || alignment == 8) ? true : false);
				if (flag && ReadInt64(chunk + (long)within * 24L) == item.Item2 && IsUserPointer(ReadInt64(chunk + ((long)within + 1L) * 24)))
				{
					if (!chunks.TryGetValue(chunkIndex, out var set))
					{
						set = (chunks[chunkIndex] = new HashSet<long>());
					}
					if (set.Add(chunk))
					{
						output.WriteLine($"  chunk[{chunkIndex}] candidate=0x{chunk:X} from item ref 0x{reference:X}");
					}
				}
			}
		}
		if (chunks.TryGetValue(0, out var zero) && chunks.TryGetValue(1, out var one))
		{
			foreach (long chunk2 in zero)
			{
				byte[] pointer2 = new byte[8];
				BinaryPrimitives.WriteInt64LittleEndian(pointer2, chunk2);
				foreach (long table in FindRemoteBytes(pointer2, 20000))
				{
					long chunk3 = ReadInt64(table + 8);
					if (one.Contains(chunk3))
					{
						output.WriteLine($"FUObject chunks table: 0x{table:X}; chunk0=0x{chunk2:X}; chunk1=0x{chunk3:X}");
						byte[] tableBytes = new byte[8];
						BinaryPrimitives.WriteInt64LittleEndian(tableBytes, table);
						foreach (long reference2 in FindRemoteBytes(tableBytes, 20000).Take(32))
						{
							byte[] around = Read(reference2 - 32, 96);
							output.WriteLine($"  table ref=0x{reference2:X}: {Convert.ToHexString(around)}");
						}
					}
				}
			}
			return;
		}
		if (!chunks.TryGetValue(0, out zero))
		{
			return;
		}
		foreach (long chunk4 in zero)
		{
			foreach (var item2 in known)
			{
				output.WriteLine($"  flat check index {item2.Item3}: 0x{ReadInt64(chunk4 + (long)item2.Item3 * 24L):X} (expected 0x{item2.Item2:X})");
			}
			byte[] pointer3 = new byte[8];
			BinaryPrimitives.WriteInt64LittleEndian(pointer3, chunk4);
			foreach (long table2 in FindRemoteBytes(pointer3, 20000).Take(32))
			{
				output.WriteLine($"  chunk table candidate=0x{table2:X}; next=0x{ReadInt64(table2 + 8):X}");
			}
		}
	}

	private IEnumerable<long> FindObjectsByNameId(uint id, long blocksBase)
	{
		byte[] needle = new byte[4];
		BinaryPrimitives.WriteUInt32LittleEndian(needle, id);
		foreach (long item in FindRemoteBytes(needle, 20000))
		{
			long candidate = item - 24;
			long vtable = ReadInt64(candidate);
			long classObject = ReadInt64(candidate + 16);
			if (vtable >= moduleBase && vtable < moduleBase + module.Length && IsUserPointer(classObject) && ReadObjectName(classObject, blocksBase) != null)
			{
				yield return candidate;
			}
		}
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
			byte[] addressBytes = new byte[8];
			BinaryPrimitives.WriteInt64LittleEndian(addressBytes, entry);
			using IEnumerator<int> enumerator2 = FindAllExact(module, addressBytes).GetEnumerator();
			if (enumerator2.MoveNext())
			{
				int reference = enumerator2.Current;
				return moduleBase + reference;
			}
		}
		throw new InvalidOperationException("UE5 FName pool was not found.");
	}

	private Dictionary<string, uint> FindNameIds(long blocksBase, IEnumerable<string> wanted)
	{
		HashSet<string> remaining = new HashSet<string>(wanted, StringComparer.Ordinal);
		Dictionary<string, uint> result = new Dictionary<string, uint>(StringComparer.Ordinal);
		for (int blockIndex = 0; blockIndex < 8192; blockIndex++)
		{
			if (remaining.Count <= 0)
			{
				break;
			}
			long block = ReadInt64(blocksBase + (long)blockIndex * 8L);
			if (!IsUserPointer(block))
			{
				break;
			}
			int byteLength;
			for (int offset = 0; offset + 2 <= 131072; offset += (2 + byteLength + 1) & -2)
			{
				ushort header = BinaryPrimitives.ReadUInt16LittleEndian(Read(block + offset, 2));
				int length = header >> 6;
				if (length == 0 || length > 1023)
				{
					break;
				}
				bool num = (header & 1) != 0;
				byteLength = (num ? (length * 2) : length);
				byte[] bytes = Read(block + offset + 2, byteLength);
				string name = (num ? Encoding.Unicode.GetString(bytes) : Encoding.ASCII.GetString(bytes));
				if (remaining.Remove(name))
				{
					result[name] = (uint)((blockIndex << 16) | (offset / 2));
					if (remaining.Count == 0)
					{
						break;
					}
				}
			}
		}
		return result;
	}

	private string? ReadObjectName(long objectAddress, long blocksBase)
	{
		uint num = BinaryPrimitives.ReadUInt32LittleEndian(Read(objectAddress + 24, 4));
		int blockIndex = (int)(num >> 16);
		int offset = (int)((num & 0xFFFF) * 2);
		if (blockIndex >= 8192 || offset >= 131072)
		{
			return null;
		}
		long block = ReadInt64(blocksBase + (long)blockIndex * 8L);
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
		if (!wide)
		{
			return Encoding.ASCII.GetString(bytes);
		}
		return Encoding.Unicode.GetString(bytes);
	}

	private void DumpPatternCandidates(string label, byte[][] patterns, TextWriter output, Func<long, string?> describe)
	{
		HashSet<long> seen = new HashSet<long>();
		foreach (byte[] pattern in patterns)
		{
			foreach (int match in FindAll(pattern))
			{
				int[] array = new int[1] { 3 };
				foreach (int extra in array)
				{
					int[] array2 = new int[2] { 4, -4 };
					foreach (int addition in array2)
					{
						int at = match + extra;
						int relative = BinaryPrimitives.ReadInt32LittleEndian(module.AsSpan(at, 4));
						long target = moduleBase + at + relative + addition;
						if (seen.Add(target))
						{
							string detail = describe(target);
							if (detail != null)
							{
								output.WriteLine($"{label}: 0x{target:X} {detail}");
							}
						}
					}
				}
			}
		}
	}

	private string? TryDescribeObjects(long address)
	{
		byte[] data = Read(address, 56);
		int max = BinaryPrimitives.ReadInt32LittleEndian(data);
		int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
		long objects = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(8));
		if (count > 5000 && count < 2000000 && max >= count && IsUserPointer(objects))
		{
			return $"flat array count={count:N0}, max={max:N0}, items=0x{objects:X}";
		}
		int[] array = new int[2] { 0, 16 };
		foreach (int offset in array)
		{
			long chunks = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset));
			int chunkMax = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 16));
			int chunkCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 20));
			int numChunks = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 28));
			if (IsUserPointer(chunks) && chunkCount > 5000 && chunkCount < 2000000 && chunkMax >= chunkCount && numChunks > 0 && numChunks < 1000)
			{
				return $"chunked array +0x{offset:X}, count={chunkCount:N0}, chunks={numChunks}, table=0x{chunks:X}";
			}
		}
		return null;
	}

	private string? TryDescribeNames(long address)
	{
		List<long> bases = new List<long> { address };
		long pointer = ReadInt64(address);
		if (IsUserPointer(pointer))
		{
			bases.Add(pointer);
		}
		foreach (long candidate in bases)
		{
			int[] array = new int[3] { 0, 16, 48 };
			foreach (int offset in array)
			{
				long blocks = candidate + offset;
				long first = ReadInt64(blocks);
				if (!IsUserPointer(first))
				{
					continue;
				}
				byte[] entry = Read(first, 16);
				ushort header = BinaryPrimitives.ReadUInt16LittleEndian(entry);
				int length = header >> 1;
				if ((length > 0 && length <= 64) || 1 == 0)
				{
					string name = (((header & 1) == 0) ? Encoding.ASCII.GetString(entry, 2, Math.Min(length, entry.Length - 2)) : Encoding.Unicode.GetString(entry, 2, Math.Min(length * 2, entry.Length - 2)));
					if (name.Equals("None", StringComparison.OrdinalIgnoreCase))
					{
						return $"blocks=0x{blocks:X} (base 0x{candidate:X}+0x{offset:X}), entry0={name}";
					}
				}
			}
		}
		return null;
	}

	private IEnumerable<int> FindAll(byte[] pattern)
	{
		for (int i = 0; i <= module.Length - pattern.Length; i++)
		{
			int j;
			for (j = 0; j < pattern.Length && (pattern[j] == 0 || pattern[j] == module[i + j]); j++)
			{
			}
			if (j == pattern.Length)
			{
				yield return i;
			}
		}
	}

	private IEnumerable<long> FindRemoteBytes(byte[] needle, int limit)
	{
		nuint address = 0u;
		byte[] buffer = new byte[4194304 + needle.Length];
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
				byte[] block = new byte[wanted];
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

	private static IEnumerable<int> FindAllExact(byte[] haystack, byte[] needle)
	{
		int start = 0;
		while (start <= haystack.Length - needle.Length)
		{
			int hit = haystack.AsSpan(start).IndexOf(needle);
			if (hit < 0)
			{
				break;
			}
			int index = start + hit;
			yield return index;
			start = index + 1;
		}
	}

	private byte[] ReadModule(int size)
	{
		byte[] result = new byte[size];
		for (int offset = 0; offset < size; offset += 65536)
		{
			int wanted = Math.Min(65536, size - offset);
			byte[] temp = new byte[wanted];
			if (ReadProcessMemory(process, (nint)(moduleBase + offset), temp, (nuint)wanted, out var read) && read != 0)
			{
				Buffer.BlockCopy(temp, 0, result, offset, (int)read);
			}
		}
		return result;
	}

	private byte[] Read(long address, int size)
	{
		byte[] value = new byte[size];
		ReadProcessMemory(process, (nint)address, value, (nuint)size, out var _);
		return value;
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

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool ReadProcessMemory(nint process, nint address, [Out] byte[] buffer, nuint size, out nuint bytesRead);

	[DllImport("kernel32.dll")]
	private static extern nuint VirtualQueryEx(nint process, nint address, out MemoryBasicInformation info, nuint length);

	[DllImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(nint handle);
}
