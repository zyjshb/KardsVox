using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace KardsVox.Core;

internal static class ReadOnlyAesKeyScanner
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

	private const uint MemCommit = 4096u;

	private const uint PageGuard = 256u;

	private const uint PageNoAccess = 1u;

	private const int ScheduleBytes = 240;

	private const int ChunkBytes = 8388608;

	private static readonly byte[] SBox = new byte[256]
	{
		99, 124, 119, 123, 242, 107, 111, 197, 48, 1,
		103, 43, 254, 215, 171, 118, 202, 130, 201, 125,
		250, 89, 71, 240, 173, 212, 162, 175, 156, 164,
		114, 192, 183, 253, 147, 38, 54, 63, 247, 204,
		52, 165, 229, 241, 113, 216, 49, 21, 4, 199,
		35, 195, 24, 150, 5, 154, 7, 18, 128, 226,
		235, 39, 178, 117, 9, 131, 44, 26, 27, 110,
		90, 160, 82, 59, 214, 179, 41, 227, 47, 132,
		83, 209, 0, 237, 32, 252, 177, 91, 106, 203,
		190, 57, 74, 76, 88, 207, 208, 239, 170, 251,
		67, 77, 51, 133, 69, 249, 2, 127, 80, 60,
		159, 168, 81, 163, 64, 143, 146, 157, 56, 245,
		188, 182, 218, 33, 16, 255, 243, 210, 205, 12,
		19, 236, 95, 151, 68, 23, 196, 167, 126, 61,
		100, 93, 25, 115, 96, 129, 79, 220, 34, 42,
		144, 136, 70, 238, 184, 20, 222, 94, 11, 219,
		224, 50, 58, 10, 73, 6, 36, 92, 194, 211,
		172, 98, 145, 149, 228, 121, 231, 200, 55, 109,
		141, 213, 78, 169, 108, 86, 244, 234, 101, 122,
		174, 8, 186, 120, 37, 46, 28, 166, 180, 198,
		232, 221, 116, 31, 75, 189, 139, 138, 112, 62,
		181, 102, 72, 3, 246, 14, 97, 53, 87, 185,
		134, 193, 29, 158, 225, 248, 152, 17, 105, 217,
		142, 148, 155, 30, 135, 233, 206, 85, 40, 223,
		140, 161, 137, 13, 191, 230, 66, 104, 65, 153,
		45, 15, 176, 84, 187, 22
	};

	private static readonly uint[] Rcon = new uint[7] { 16777216u, 33554432u, 67108864u, 134217728u, 268435456u, 536870912u, 1073741824u };

	public static IReadOnlyList<string> ScanKards(Action<string>? progress = null)
	{
		Process[] processesByName = Process.GetProcessesByName("kards-Win64-Shipping");
		if (processesByName.Length == 0)
		{
			throw new InvalidOperationException("KARDS is not running.");
		}
		using Process process = processesByName.OrderByDescending((Process p) => p.WorkingSet64).First();
		return ScanProcess(process.Id, progress);
	}

	public static IReadOnlyList<string> ScanProcess(int processId, Action<string>? progress = null)
	{
		nint process = OpenProcess(1040u, inheritHandle: false, processId);
		if (process == IntPtr.Zero)
		{
			throw new InvalidOperationException($"Could not open process {processId} for read-only scanning (error {Marshal.GetLastWin32Error()}).");
		}
		try
		{
			HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			nuint address = 0u;
			ulong scanned = 0uL;
			ulong nextProgress = 536870912uL;
			MemoryBasicInformation info;
			while (VirtualQueryEx(process, (nint)address, out info, (nuint)Marshal.SizeOf<MemoryBasicInformation>()) != 0)
			{
				nuint baseAddress = (nuint)info.BaseAddress;
				nuint regionSize = info.RegionSize;
				nuint next = baseAddress + regionSize;
				if (next <= address)
				{
					break;
				}
				address = next;
				if (info.State == 4096 && IsReadable(info.Protect) && regionSize >= 240)
				{
					ScanRegion(process, baseAddress, regionSize, keys, ref scanned);
					if (scanned >= nextProgress)
					{
						progress?.Invoke($"Read-only memory scanned: {scanned / 1024 / 1024:N0} MB; candidates: {keys.Count}");
						nextProgress = scanned + 536870912;
					}
				}
			}
			return keys.ToArray();
		}
		finally
		{
			CloseHandle(process);
		}
	}

	private static void ScanRegion(nint process, nuint start, nuint size, HashSet<string> keys, ref ulong scanned)
	{
		byte[] buffer = new byte[8388848];
		nuint offset = 0u;
		int carry = 0;
		while (offset < size)
		{
			int wanted = (int)Math.Min(8388608u, size - offset);
			byte[] block = new byte[wanted];
			if (!ReadProcessMemory(process, (nint)(start + offset), block, (nuint)wanted, out var read) || read == 0)
			{
				offset += (nuint)wanted;
				carry = 0;
				continue;
			}
			Buffer.BlockCopy(block, 0, buffer, carry, (int)read);
			int count = carry + (int)read;
			ScanBuffer(buffer.AsSpan(0, count), keys);
			carry = Math.Min(240, count);
			buffer.AsSpan(count - carry, carry).CopyTo(buffer);
			offset += read;
			scanned += read;
		}
	}

	internal static void ScanBuffer(ReadOnlySpan<byte> bytes, HashSet<string> keys)
	{
		for (int offset = 0; offset <= bytes.Length - 240; offset += 4)
		{
			ReadOnlySpan<byte> schedule = bytes.Slice(offset, 240);
			if (TryReadKey(schedule, littleEndianWords: false, out byte[] key) || TryReadKey(schedule, littleEndianWords: true, out key) || TryReadDecryptionKey(schedule, littleEndianWords: false, out key) || TryReadDecryptionKey(schedule, littleEndianWords: true, out key))
			{
				keys.Add(Convert.ToHexString(key));
			}
		}
	}

	private static bool TryReadKey(ReadOnlySpan<byte> schedule, bool littleEndianWords, out byte[] key)
	{
		Span<uint> first = stackalloc uint[12];
		for (int i = 0; i < first.Length; i++)
		{
			first[i] = ReadWord(schedule, i, littleEndianWords);
		}
		if (first[8] != (first[0] ^ SubRot(first[7]) ^ Rcon[0]))
		{
			key = Array.Empty<byte>();
			return false;
		}
		Span<uint> words = stackalloc uint[60];
		ReadWords(schedule, littleEndianWords, words);
		for (int j = 8; j < 60; j++)
		{
			uint temp = words[j - 1];
			if (j % 8 == 0)
			{
				temp = SubRot(temp) ^ Rcon[j / 8 - 1];
			}
			else if (j % 8 == 4)
			{
				temp = SubWord(temp);
			}
			if (words[j] != (words[j - 8] ^ temp))
			{
				key = Array.Empty<byte>();
				return false;
			}
		}
		key = WordsToKey(words);
		return true;
	}

	private static bool TryReadDecryptionKey(ReadOnlySpan<byte> schedule, bool littleEndianWords, out byte[] key)
	{
		Span<uint> initial = stackalloc uint[8];
		for (int i = 0; i < 4; i++)
		{
			initial[i] = ReadWord(schedule, 56 + i, littleEndianWords);
		}
		for (int j = 0; j < 4; j++)
		{
			initial[4 + j] = MixColumn(ReadWord(schedule, 52 + j, littleEndianWords));
		}
		if ((initial[0] ^ SubRot(initial[7]) ^ Rcon[0]) == MixColumn(ReadWord(schedule, 48, littleEndianWords)))
		{
			Span<uint> actual = stackalloc uint[60];
			ReadWords(schedule, littleEndianWords, actual);
			uint[] expanded = Expand(initial);
			if (MatchesReverseDecryption(actual, expanded))
			{
				key = WordsToKey(initial);
				return true;
			}
		}
		for (int k = 0; k < 4; k++)
		{
			initial[k] = ReadWord(schedule, k, littleEndianWords);
		}
		for (int l = 0; l < 4; l++)
		{
			initial[4 + l] = MixColumn(ReadWord(schedule, 4 + l, littleEndianWords));
		}
		if ((initial[0] ^ SubRot(initial[7]) ^ Rcon[0]) == MixColumn(ReadWord(schedule, 8, littleEndianWords)))
		{
			Span<uint> actual2 = stackalloc uint[60];
			ReadWords(schedule, littleEndianWords, actual2);
			uint[] expanded2 = Expand(initial);
			if (MatchesForwardDecryption(actual2, expanded2))
			{
				key = WordsToKey(initial);
				return true;
			}
		}
		key = Array.Empty<byte>();
		return false;
	}

	private static void ReadWords(ReadOnlySpan<byte> schedule, bool littleEndianWords, Span<uint> words)
	{
		for (int i = 0; i < words.Length; i++)
		{
			words[i] = ReadWord(schedule, i, littleEndianWords);
		}
	}

	private static uint ReadWord(ReadOnlySpan<byte> schedule, int index, bool littleEndianWords)
	{
		if (!littleEndianWords)
		{
			return BinaryPrimitives.ReadUInt32BigEndian(schedule.Slice(index * 4, 4));
		}
		return BinaryPrimitives.ReadUInt32LittleEndian(schedule.Slice(index * 4, 4));
	}

	private static byte[] WordsToKey(ReadOnlySpan<uint> words)
	{
		byte[] key = new byte[32];
		for (int i = 0; i < 8; i++)
		{
			BinaryPrimitives.WriteUInt32BigEndian(key.AsSpan(i * 4, 4), words[i]);
		}
		return key;
	}

	private static uint[] Expand(ReadOnlySpan<uint> key)
	{
		uint[] words = new uint[60];
		key.CopyTo(words);
		for (int i = 8; i < words.Length; i++)
		{
			uint temp = words[i - 1];
			if (i % 8 == 0)
			{
				temp = SubRot(temp) ^ Rcon[i / 8 - 1];
			}
			else if (i % 8 == 4)
			{
				temp = SubWord(temp);
			}
			words[i] = words[i - 8] ^ temp;
		}
		return words;
	}

	private static bool MatchesForwardDecryption(ReadOnlySpan<uint> actual, ReadOnlySpan<uint> expanded)
	{
		for (int i = 0; i < 60; i++)
		{
			uint expected = ((i < 4 || i >= 56) ? expanded[i] : InvMixColumn(expanded[i]));
			if (actual[i] != expected)
			{
				return false;
			}
		}
		return true;
	}

	private static bool MatchesReverseDecryption(ReadOnlySpan<uint> actual, ReadOnlySpan<uint> expanded)
	{
		for (int round = 0; round <= 14; round++)
		{
			int source = (14 - round) * 4;
			for (int column = 0; column < 4; column++)
			{
				bool flag = ((round == 0 || round == 14) ? true : false);
				uint expected = (flag ? expanded[source + column] : InvMixColumn(expanded[source + column]));
				if (actual[round * 4 + column] != expected)
				{
					return false;
				}
			}
		}
		return true;
	}

	private static uint MixColumn(uint value)
	{
		byte a = (byte)(value >> 24);
		byte b = (byte)(value >> 16);
		byte c = (byte)(value >> 8);
		byte d = (byte)value;
		byte a2 = Xtime(a);
		byte b2 = Xtime(b);
		byte c2 = Xtime(c);
		byte d2 = Xtime(d);
		return Pack((byte)(a2 ^ (b2 ^ b) ^ c ^ d), (byte)(a ^ b2 ^ (c2 ^ c) ^ d), (byte)(a ^ b ^ c2 ^ (d2 ^ d)), (byte)(a2 ^ a ^ b ^ c ^ d2));
	}

	private static uint InvMixColumn(uint value)
	{
		byte a = (byte)(value >> 24);
		byte b = (byte)(value >> 16);
		byte c = (byte)(value >> 8);
		byte d = (byte)value;
		return Pack((byte)(Mul(a, 14) ^ Mul(b, 11) ^ Mul(c, 13) ^ Mul(d, 9)), (byte)(Mul(a, 9) ^ Mul(b, 14) ^ Mul(c, 11) ^ Mul(d, 13)), (byte)(Mul(a, 13) ^ Mul(b, 9) ^ Mul(c, 14) ^ Mul(d, 11)), (byte)(Mul(a, 11) ^ Mul(b, 13) ^ Mul(c, 9) ^ Mul(d, 14)));
	}

	private static byte Mul(byte value, int factor)
	{
		int result = 0;
		int current = value;
		while (factor != 0)
		{
			if ((factor & 1) != 0)
			{
				result ^= current;
			}
			current = (current << 1) ^ (((current & 0x80) != 0) ? 283 : 0);
			factor >>= 1;
		}
		return (byte)result;
	}

	private static byte Xtime(byte value)
	{
		return (byte)((value << 1) ^ (((value & 0x80) != 0) ? 27 : 0));
	}

	private static uint Pack(byte a, byte b, byte c, byte d)
	{
		return (uint)((a << 24) | (b << 16) | (c << 8) | d);
	}

	private static uint SubRot(uint value)
	{
		return SubWord((value << 8) | (value >> 24));
	}

	private static uint SubWord(uint value)
	{
		return (uint)((SBox[(value >> 24) & 0xFF] << 24) | (SBox[(value >> 16) & 0xFF] << 16) | (SBox[(value >> 8) & 0xFF] << 8) | SBox[value & 0xFF]);
	}

	private static bool IsReadable(uint protect)
	{
		if ((protect & 0x101) == 0)
		{
			return (protect & 0xEE) != 0;
		}
		return false;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern nint OpenProcess(uint access, bool inheritHandle, int processId);

	[DllImport("kernel32.dll")]
	private static extern nuint VirtualQueryEx(nint process, nint address, out MemoryBasicInformation info, nuint length);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool ReadProcessMemory(nint process, nint address, [Out] byte[] buffer, nuint size, out nuint bytesRead);

	[DllImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(nint handle);
}
