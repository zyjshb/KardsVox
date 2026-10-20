using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KardsVox.Core;

/// <summary>
/// Finds an AES-256 encryption key schedule in another process. The process is
/// opened with query/read rights only; this class has no write or injection API.
/// </summary>
internal static class ReadOnlyAesKeyScanner
{
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint MemCommit = 0x1000;
    private const uint PageGuard = 0x100;
    private const uint PageNoAccess = 0x01;
    private const int ScheduleBytes = 240;
    private const int ChunkBytes = 8 * 1024 * 1024;

    private static readonly byte[] SBox =
    [
        0x63,0x7c,0x77,0x7b,0xf2,0x6b,0x6f,0xc5,0x30,0x01,0x67,0x2b,0xfe,0xd7,0xab,0x76,
        0xca,0x82,0xc9,0x7d,0xfa,0x59,0x47,0xf0,0xad,0xd4,0xa2,0xaf,0x9c,0xa4,0x72,0xc0,
        0xb7,0xfd,0x93,0x26,0x36,0x3f,0xf7,0xcc,0x34,0xa5,0xe5,0xf1,0x71,0xd8,0x31,0x15,
        0x04,0xc7,0x23,0xc3,0x18,0x96,0x05,0x9a,0x07,0x12,0x80,0xe2,0xeb,0x27,0xb2,0x75,
        0x09,0x83,0x2c,0x1a,0x1b,0x6e,0x5a,0xa0,0x52,0x3b,0xd6,0xb3,0x29,0xe3,0x2f,0x84,
        0x53,0xd1,0x00,0xed,0x20,0xfc,0xb1,0x5b,0x6a,0xcb,0xbe,0x39,0x4a,0x4c,0x58,0xcf,
        0xd0,0xef,0xaa,0xfb,0x43,0x4d,0x33,0x85,0x45,0xf9,0x02,0x7f,0x50,0x3c,0x9f,0xa8,
        0x51,0xa3,0x40,0x8f,0x92,0x9d,0x38,0xf5,0xbc,0xb6,0xda,0x21,0x10,0xff,0xf3,0xd2,
        0xcd,0x0c,0x13,0xec,0x5f,0x97,0x44,0x17,0xc4,0xa7,0x7e,0x3d,0x64,0x5d,0x19,0x73,
        0x60,0x81,0x4f,0xdc,0x22,0x2a,0x90,0x88,0x46,0xee,0xb8,0x14,0xde,0x5e,0x0b,0xdb,
        0xe0,0x32,0x3a,0x0a,0x49,0x06,0x24,0x5c,0xc2,0xd3,0xac,0x62,0x91,0x95,0xe4,0x79,
        0xe7,0xc8,0x37,0x6d,0x8d,0xd5,0x4e,0xa9,0x6c,0x56,0xf4,0xea,0x65,0x7a,0xae,0x08,
        0xba,0x78,0x25,0x2e,0x1c,0xa6,0xb4,0xc6,0xe8,0xdd,0x74,0x1f,0x4b,0xbd,0x8b,0x8a,
        0x70,0x3e,0xb5,0x66,0x48,0x03,0xf6,0x0e,0x61,0x35,0x57,0xb9,0x86,0xc1,0x1d,0x9e,
        0xe1,0xf8,0x98,0x11,0x69,0xd9,0x8e,0x94,0x9b,0x1e,0x87,0xe9,0xce,0x55,0x28,0xdf,
        0x8c,0xa1,0x89,0x0d,0xbf,0xe6,0x42,0x68,0x41,0x99,0x2d,0x0f,0xb0,0x54,0xbb,0x16
    ];

    private static readonly uint[] Rcon =
    [0x01000000,0x02000000,0x04000000,0x08000000,0x10000000,0x20000000,0x40000000];

    public static IReadOnlyList<string> ScanKards(Action<string>? progress = null)
    {
        var processes = Process.GetProcessesByName("kards-Win64-Shipping");
        if (processes.Length == 0) throw new InvalidOperationException("KARDS is not running.");
        using var process = processes.OrderByDescending(p => p.WorkingSet64).First();
        return ScanProcess(process.Id, progress);
    }

    public static IReadOnlyList<string> ScanProcess(int processId, Action<string>? progress = null)
    {
        IntPtr process = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
        if (process == IntPtr.Zero)
            throw new InvalidOperationException($"Could not open process {processId} for read-only scanning (error {Marshal.GetLastWin32Error()}).");

        try
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            nuint address = 0;
            ulong scanned = 0;
            ulong nextProgress = 512UL * 1024 * 1024;
            while (VirtualQueryEx(process, (IntPtr)address, out var info, (nuint)Marshal.SizeOf<MemoryBasicInformation>()) != 0)
            {
                nuint baseAddress = (nuint)info.BaseAddress;
                nuint regionSize = info.RegionSize;
                nuint next = baseAddress + regionSize;
                if (next <= address) break;
                address = next;

                if (info.State != MemCommit || !IsReadable(info.Protect) || regionSize < ScheduleBytes) continue;
                ScanRegion(process, baseAddress, regionSize, keys, ref scanned);
                if (scanned >= nextProgress)
                {
                    progress?.Invoke($"Read-only memory scanned: {scanned / 1024 / 1024:N0} MB; candidates: {keys.Count}");
                    nextProgress = scanned + 512UL * 1024 * 1024;
                }
            }
            return keys.ToArray();
        }
        finally { CloseHandle(process); }
    }

    private static void ScanRegion(IntPtr process, nuint start, nuint size, HashSet<string> keys, ref ulong scanned)
    {
        byte[] buffer = new byte[ChunkBytes + ScheduleBytes];
        nuint offset = 0;
        int carry = 0;
        while (offset < size)
        {
            int wanted = (int)Math.Min((nuint)ChunkBytes, size - offset);
            byte[] block = new byte[wanted];
            if (!ReadProcessMemory(process, (IntPtr)(start + offset), block, (nuint)wanted, out nuint read) || read == 0)
            {
                offset += (nuint)wanted;
                carry = 0;
                continue;
            }
            Buffer.BlockCopy(block, 0, buffer, carry, (int)read);
            int count = carry + (int)read;
            ScanBuffer(buffer.AsSpan(0, count), keys);
            carry = Math.Min(ScheduleBytes, count);
            buffer.AsSpan(count - carry, carry).CopyTo(buffer);
            offset += read;
            scanned += (ulong)read;
        }
    }

    internal static void ScanBuffer(ReadOnlySpan<byte> bytes, HashSet<string> keys)
    {
        for (int offset = 0; offset <= bytes.Length - ScheduleBytes; offset += 4)
        {
            var schedule = bytes.Slice(offset, ScheduleBytes);
            if (TryReadKey(schedule, false, out byte[]? key) ||
                TryReadKey(schedule, true, out key) ||
                TryReadDecryptionKey(schedule, false, out key) ||
                TryReadDecryptionKey(schedule, true, out key))
                keys.Add(Convert.ToHexString(key));
        }
    }

    private static bool TryReadKey(ReadOnlySpan<byte> schedule, bool littleEndianWords, out byte[] key)
    {
        Span<uint> first = stackalloc uint[12];
        for (int i = 0; i < first.Length; i++) first[i] = ReadWord(schedule, i, littleEndianWords);
        if (first[8] != (first[0] ^ SubRot(first[7]) ^ Rcon[0])) { key = []; return false; }

        Span<uint> words = stackalloc uint[60];
        ReadWords(schedule, littleEndianWords, words);

        for (int i = 8; i < 60; i++)
        {
            uint temp = words[i - 1];
            if (i % 8 == 0) temp = SubRot(temp) ^ Rcon[i / 8 - 1];
            else if (i % 8 == 4) temp = SubWord(temp);
            if (words[i] != (words[i - 8] ^ temp)) { key = []; return false; }
        }

        key = WordsToKey(words);
        return true;
    }

    private static bool TryReadDecryptionKey(ReadOnlySpan<byte> schedule, bool littleEndianWords, out byte[] key)
    {
        Span<uint> initial = stackalloc uint[8];
        for (int i = 0; i < 4; i++) initial[i] = ReadWord(schedule, 56 + i, littleEndianWords);
        for (int i = 0; i < 4; i++) initial[4 + i] = MixColumn(ReadWord(schedule, 52 + i, littleEndianWords));
        uint firstNext = initial[0] ^ SubRot(initial[7]) ^ Rcon[0];
        if (firstNext == MixColumn(ReadWord(schedule, 48, littleEndianWords)))
        {
            Span<uint> actual = stackalloc uint[60];
            ReadWords(schedule, littleEndianWords, actual);
            uint[] expanded = Expand(initial);
            if (MatchesReverseDecryption(actual, expanded)) { key = WordsToKey(initial); return true; }
        }

        for (int i = 0; i < 4; i++) initial[i] = ReadWord(schedule, i, littleEndianWords);
        for (int i = 0; i < 4; i++) initial[4 + i] = MixColumn(ReadWord(schedule, 4 + i, littleEndianWords));
        firstNext = initial[0] ^ SubRot(initial[7]) ^ Rcon[0];
        if (firstNext == MixColumn(ReadWord(schedule, 8, littleEndianWords)))
        {
            Span<uint> actual = stackalloc uint[60];
            ReadWords(schedule, littleEndianWords, actual);
            uint[] expanded = Expand(initial);
            if (MatchesForwardDecryption(actual, expanded)) { key = WordsToKey(initial); return true; }
        }

        key = [];
        return false;
    }

    private static void ReadWords(ReadOnlySpan<byte> schedule, bool littleEndianWords, Span<uint> words)
    {
        for (int i = 0; i < words.Length; i++)
            words[i] = ReadWord(schedule, i, littleEndianWords);
    }

    private static uint ReadWord(ReadOnlySpan<byte> schedule, int index, bool littleEndianWords) =>
        littleEndianWords
            ? BinaryPrimitives.ReadUInt32LittleEndian(schedule.Slice(index * 4, 4))
            : BinaryPrimitives.ReadUInt32BigEndian(schedule.Slice(index * 4, 4));

    private static byte[] WordsToKey(ReadOnlySpan<uint> words)
    {
        byte[] key = new byte[32];
        for (int i = 0; i < 8; i++) BinaryPrimitives.WriteUInt32BigEndian(key.AsSpan(i * 4, 4), words[i]);
        return key;
    }

    private static uint[] Expand(ReadOnlySpan<uint> key)
    {
        uint[] words = new uint[60];
        key.CopyTo(words);
        for (int i = 8; i < words.Length; i++)
        {
            uint temp = words[i - 1];
            if (i % 8 == 0) temp = SubRot(temp) ^ Rcon[i / 8 - 1];
            else if (i % 8 == 4) temp = SubWord(temp);
            words[i] = words[i - 8] ^ temp;
        }
        return words;
    }

    private static bool MatchesForwardDecryption(ReadOnlySpan<uint> actual, ReadOnlySpan<uint> expanded)
    {
        for (int i = 0; i < 60; i++)
        {
            uint expected = i < 4 || i >= 56 ? expanded[i] : InvMixColumn(expanded[i]);
            if (actual[i] != expected) return false;
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
                uint expected = round is 0 or 14 ? expanded[source + column] : InvMixColumn(expanded[source + column]);
                if (actual[round * 4 + column] != expected) return false;
            }
        }
        return true;
    }

    private static uint MixColumn(uint value)
    {
        byte a = (byte)(value >> 24), b = (byte)(value >> 16), c = (byte)(value >> 8), d = (byte)value;
        byte a2 = Xtime(a), b2 = Xtime(b), c2 = Xtime(c), d2 = Xtime(d);
        return Pack((byte)(a2^(b2^b)^c^d), (byte)(a^b2^(c2^c)^d),
            (byte)(a^b^c2^(d2^d)), (byte)((a2^a)^b^c^d2));
    }

    private static uint InvMixColumn(uint value)
    {
        byte a = (byte)(value >> 24), b = (byte)(value >> 16), c = (byte)(value >> 8), d = (byte)value;
        return Pack((byte)(Mul(a,14)^Mul(b,11)^Mul(c,13)^Mul(d,9)), (byte)(Mul(a,9)^Mul(b,14)^Mul(c,11)^Mul(d,13)),
            (byte)(Mul(a,13)^Mul(b,9)^Mul(c,14)^Mul(d,11)), (byte)(Mul(a,11)^Mul(b,13)^Mul(c,9)^Mul(d,14)));
    }

    private static byte Mul(byte value, int factor)
    {
        int result = 0, current = value;
        while (factor != 0)
        {
            if ((factor & 1) != 0) result ^= current;
            current = (current << 1) ^ ((current & 0x80) != 0 ? 0x11b : 0);
            factor >>= 1;
        }
        return (byte)result;
    }

    private static byte Xtime(byte value) => (byte)((value << 1) ^ ((value & 0x80) != 0 ? 0x1b : 0));

    private static uint Pack(byte a, byte b, byte c, byte d) => (uint)(a << 24 | b << 16 | c << 8 | d);

    private static uint SubRot(uint value) => SubWord((value << 8) | (value >> 24));
    private static uint SubWord(uint value) =>
        (uint)(SBox[(value >> 24) & 255] << 24 | SBox[(value >> 16) & 255] << 16 |
               SBox[(value >> 8) & 255] << 8 | SBox[value & 255]);

    private static bool IsReadable(uint protect) =>
        (protect & (PageGuard | PageNoAccess)) == 0 &&
        (protect & (0x02u | 0x04u | 0x08u | 0x20u | 0x40u | 0x80u)) != 0;

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);
    [DllImport("kernel32.dll")]
    private static extern nuint VirtualQueryEx(IntPtr process, IntPtr address, out MemoryBasicInformation info, nuint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, [Out] byte[] buffer, nuint size, out nuint bytesRead);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
