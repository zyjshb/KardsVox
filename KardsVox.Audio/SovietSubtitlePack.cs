using NAudio.Wave;

namespace KardsVox.Audio;

internal sealed record SovietSubtitleLine(string Key, string Speaker, string Original, string Chinese,
    string AudioPath, TimeSpan AudioDuration);

// Russian audio uses the same key as each editable Russian/Chinese subtitle.
internal sealed class SovietSubtitlePack
{
    private readonly Dictionary<string, SovietSubtitleLine> exact = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SovietSubtitleLine> orders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SubtitlePool> unitPools = new(StringComparer.OrdinalIgnoreCase);

    public int OrderCount => orders.Count;
    public int AudioCount => exact.Count + orders.Count;
    public int GetUnitPoolSize(string category, string trigger = "DEPLOY") =>
        unitPools.TryGetValue(category + "/" + trigger, out SubtitlePool? pool)
            ? pool.Count : throw new KeyNotFoundException($"Unknown Soviet unit pool: {category}/{trigger}");

    public SovietSubtitlePack(string runtimeRoot)
    {
        string audioRoot = Path.Combine(runtimeRoot, "Audio", "Soviet");
        string path = Path.Combine(runtimeRoot, "Data", "Soviet", "subtitle_lines.tsv");
        if (!File.Exists(path)) throw new FileNotFoundException("Soviet subtitle lines are missing", path);
        foreach (string row in File.ReadLines(path).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(row)) continue;
            string[] columns = row.Split('\t');
            if (columns.Length != 5 || columns.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("Invalid Soviet subtitle row: " + row);
            string key = columns[0] + "/" + columns[1];
            if (!exact.TryAdd(key, LoadLine(key, columns[2], columns[3], columns[4],
                    Path.Combine(audioRoot, "SU_SUB_" + columns[0] + "_" + columns[1] + ".wav"))))
                throw new InvalidDataException("Duplicate Soviet subtitle row: " + row);
        }
        foreach (string required in new[] { "CMD/HQ_LOW", "CMD/HQ_CRITICAL", "CMD/ENEMY_HQ_LOW",
                     "INFANTRY/DEPLOY", "TANK/DEPLOY", "AIR/DEPLOY", "ARTILLERY/DEPLOY", "CAVALRY/DEPLOY",
                     "CMD/INTRO_1", "CMD/INTRO_2", "CMD/INTRO_3", "CMD/INTRO_4", "CMD/INTRO_5" })
            if (!exact.ContainsKey(required)) throw new InvalidDataException("Soviet subtitle missing: " + required);
        foreach (string category in new[] { "INFANTRY", "TANK", "AIR", "ARTILLERY", "CAVALRY" })
        foreach (string trigger in new[] { "DEPLOY", "HURT", "ATTACK" })
        {
            string key = category + "/" + trigger;
            var lines = exact.Where(entry => entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                    entry.Key.StartsWith(key + "_", StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .Select(entry => entry.Value).ToArray();
            if (lines.Length < 2 || lines.Select(line => line.Original).Distinct(StringComparer.Ordinal).Count() != lines.Length ||
                lines.Select(line => line.Chinese).Distinct(StringComparer.Ordinal).Count() != lines.Length ||
                lines.Select(line => line.Speaker).Distinct(StringComparer.Ordinal).Count() != 1)
                throw new InvalidDataException("Incomplete or reused Soviet unit subtitles: " + key);
            unitPools.Add(key, new SubtitlePool(lines));
        }

        string ordersPath = Path.Combine(runtimeRoot, "Data", "Soviet", "order_lines.tsv");
        if (!File.Exists(ordersPath)) throw new FileNotFoundException("Soviet order subtitles are missing", ordersPath);
        HashSet<string> originals = new(StringComparer.Ordinal);
        HashSet<string> chinese = new(StringComparer.Ordinal);
        foreach (string row in File.ReadLines(ordersPath).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(row)) continue;
            string[] columns = row.Split('\t');
            if (columns.Length != 3 || columns.Any(string.IsNullOrWhiteSpace) ||
                !columns[0].StartsWith("card_event_", StringComparison.OrdinalIgnoreCase) ||
                !orders.TryAdd(columns[0], LoadLine(columns[0], "通信兵", columns[1], columns[2],
                    Path.Combine(audioRoot, "SU_ORDER_" + columns[0] + ".wav"))) ||
                !originals.Add(columns[1]) || !chinese.Add(columns[2]))
                throw new InvalidDataException("Invalid, duplicate, or reused Soviet order subtitle: " + row);
        }
        string catalogPath = Path.Combine(runtimeRoot, "Data", "Soviet", "card_catalog.tsv");
        HashSet<string> catalogOrders = File.ReadLines(catalogPath).Skip(1)
            .Select(row => row.Split('\t'))
            .Where(columns => columns.Length == 2 && columns[1] == "ORDER")
            .Select(columns => columns[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (catalogOrders.Count != 119 || orders.Count != catalogOrders.Count ||
            orders.Keys.Any(resource => !catalogOrders.Contains(resource)))
            throw new InvalidDataException($"Incomplete Soviet order subtitles: {orders.Count}/{catalogOrders.Count}/119");
        if (exact.Count != 55 || AudioCount != 174)
            throw new InvalidDataException($"Incomplete Soviet audio pack: {exact.Count}+{orders.Count}/174");
    }

    private static SovietSubtitleLine LoadLine(string key, string speaker, string original, string chinese, string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Soviet voice clip is missing: " + key, path);
        using WaveFileReader wav = new(path);
        WaveFormat format = wav.WaveFormat;
        if (format.Encoding != WaveFormatEncoding.Pcm || format.SampleRate != 48000 ||
            format.Channels != 1 || format.BitsPerSample != 16 || wav.TotalTime < TimeSpan.FromMilliseconds(400))
            throw new InvalidDataException("Invalid Soviet voice clip (48 kHz mono 16-bit PCM required): " + path);
        return new SovietSubtitleLine(key, speaker, original, chinese, path, wav.TotalTime);
    }

    public IReadOnlyList<SovietSubtitleLine> IntroLines =>
        Enumerable.Range(1, 5).Select(index =>
            exact["CMD/INTRO_" + index]).ToArray();

    public SovietSubtitleLine Pick(string category, string eventName, string? resource = null)
    {
        if (eventName == "INTRO") eventName = "INTRO_1";
        if (category == "ORDER")
        {
            string key = NormalizeResource(resource);
            if (orders.TryGetValue(key, out var order)) return order;
            throw new InvalidDataException("Unmapped Soviet order: " + (resource ?? "<empty>"));
        }
        if (unitPools.TryGetValue(category + "/" + eventName, out SubtitlePool? pool)) return pool.Next();
        if (exact.TryGetValue(category + "/" + eventName, out var line)) return line;
        if (exact.TryGetValue(category + "/*", out line)) return line;
        return exact["INFANTRY/DEPLOY"];
    }

    private static string NormalizeResource(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string text = value.ToLowerInvariant();
        int start = text.IndexOf("card_", StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        int end = start;
        while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] == '_')) end++;
        string result = text[start..end];
        return result.EndsWith("_c", StringComparison.Ordinal) ? result[..^2] : result;
    }

    private sealed class SubtitlePool(SovietSubtitleLine[] lines)
    {
        private readonly int[] order = new int[lines.Length];
        private int position = lines.Length;
        private int previous = -1;

        public int Count => lines.Length;

        public SovietSubtitleLine Next()
        {
            lock (order)
            {
                if (position >= order.Length) Refill();
                int index = order[position++];
                previous = index;
                return lines[index];
            }
        }

        private void Refill()
        {
            for (int i = 0; i < order.Length; i++) order[i] = i;
            for (int i = order.Length - 1; i > 0; i--)
            {
                int swap = Random.Shared.Next(i + 1);
                (order[i], order[swap]) = (order[swap], order[i]);
            }
            if (order[0] == previous)
            {
                int swap = Random.Shared.Next(1, order.Length);
                (order[0], order[swap]) = (order[swap], order[0]);
            }
            position = 0;
        }
    }
}
