using System.Text.RegularExpressions;
using KardsVox.Core;
using NAudio.Wave;

namespace KardsVox.Audio;

internal sealed record NationSubtitleLine(string Key, string Speaker, string Original, string Chinese,
    string AudioPath, TimeSpan AudioDuration, string AudioStatus)
{
    public bool HasAudio => AudioStatus == "ready";
    public TimeSpan ReadingDuration => TimeSpan.FromSeconds(Math.Clamp(
        Math.Max(Original.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length / 2.7,
            Chinese.Length / 5.0) + .8, 2.8, 12));
    public TimeSpan Duration(bool audio) => audio && HasAudio ? AudioDuration : ReadingDuration;
}

// New nations share this loader. Only the two TSVs are editable content sources;
// the generated manifest is a delivery/checklist artifact, never a second source.
internal sealed class NationSubtitlePack
{
    private readonly Dictionary<string, NationSubtitleLine> exact = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NationSubtitleLine> orders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LinePool> pools = new(StringComparer.OrdinalIgnoreCase);
    public VoicePackDefinition Definition { get; }
    public NationCardCatalog Cards { get; }
    public IReadOnlyList<NationSubtitleLine> IntroLines { get; }
    public IEnumerable<NationSubtitleLine> AllLines => exact.Values.Concat(orders.Values);
    public int OrderCount => orders.Count;
    public int AudioCount => AllLines.Count(line => line.HasAudio);
    public int MissingCount => AllLines.Count(line => line.AudioStatus == "pending");
    public int InvalidCount => AllLines.Count(line => line.AudioStatus.StartsWith("invalid:"));
    public IEnumerable<string> UnitCategories => Cards.Resources.Values
        .Where(category => category is not ("ORDER" or "LOCATION")).Distinct().Order();

    public NationSubtitlePack(string root, VoicePackDefinition definition)
    {
        Definition = definition;
        Cards = new NationCardCatalog(root, definition.Id, definition.CardCount);
        string data = Path.Combine(root, "Data", definition.Id);
        string audio = Path.Combine(root, "Audio", definition.Id);
        foreach (string[] c in ReadTable(Path.Combine(data, "subtitle_lines.tsv"),
                     "category\ttrigger\tspeaker\toriginal\ttext"))
        {
            if (c[0] != "CMD" && !UnitCategories.Contains(c[0]) ||
                !Regex.IsMatch(c[1], @"^(INTRO_[1-9][0-9]*|DEPLOY(?:_[2-9][0-9]*)?|ATTACK(?:_[2-9][0-9]*)?|HURT(?:_[2-9][0-9]*)?|MOVE(?:_[2-9][0-9]*)?|HQ_LOW|HQ_CRITICAL|ENEMY_HQ_LOW)$"))
                throw new InvalidDataException($"Invalid {definition.Id} event: {c[0]}/{c[1]}");
            string key = c[0] + "/" + c[1];
            if (!exact.TryAdd(key, LoadLine(key, c[2], c[3], c[4],
                    Path.Combine(audio, definition.AudioPrefix + "_SUB_" + c[0] + "_" + c[1] + ".wav"))))
                throw new InvalidDataException("Duplicate subtitle key: " + key);
        }
        foreach (string required in new[] { "CMD/HQ_LOW", "CMD/HQ_CRITICAL", "CMD/ENEMY_HQ_LOW", "CMD/INTRO_1" })
            if (!exact.ContainsKey(required)) throw new InvalidDataException(definition.Id + " missing " + required);
        IntroLines = exact.Where(p => p.Key.StartsWith("CMD/INTRO_", StringComparison.Ordinal))
            .OrderBy(p => int.Parse(p.Key[10..])).Select(p => p.Value).ToArray();
        if (IntroLines.Where((line, i) => line.Key != "CMD/INTRO_" + (i + 1)).Any())
            throw new InvalidDataException("Intro segment numbers must be consecutive.");
        foreach (string category in UnitCategories)
        foreach (string trigger in new[] { "DEPLOY", "ATTACK", "HURT", "MOVE" })
        {
            string key = category + "/" + trigger;
            var lines = exact.Values.Where(line => line.Key == key || line.Key.StartsWith(key + "_", StringComparison.Ordinal)).ToArray();
            int minimum = trigger == "DEPLOY" ? 4 : 2;
            if (lines.Length < minimum || lines.Select(line => line.Original).Distinct().Count() != lines.Length ||
                lines.Select(line => line.Chinese).Distinct().Count() != lines.Length ||
                lines.Select(line => line.Speaker).Distinct().Count() != 1)
                throw new InvalidDataException("Incomplete or repeated unit pool: " + key);
            pools.Add(key, new LinePool(lines));
        }
        foreach (string[] c in ReadTable(Path.Combine(data, "order_lines.tsv"), "resource\toriginal\tchinese"))
        {
            if (!Regex.IsMatch(c[0], @"^card_[a-z0-9_]+$") || Cards.Lookup(c[0]) != "ORDER" ||
                !orders.TryAdd(c[0], LoadLine(c[0], definition.Name + "通信兵", c[1], c[2],
                    Path.Combine(audio, definition.AudioPrefix + "_ORDER_" + c[0] + ".wav"))))
                throw new InvalidDataException("Invalid or duplicate order: " + c[0]);
        }
        string[] requiredOrders = Cards.Resources.Where(p => p.Value == "ORDER").Select(p => p.Key).ToArray();
        if (!requiredOrders.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(orders.Keys))
            throw new InvalidDataException("Missing order lines: " + string.Join(", ", requiredOrders.Except(orders.Keys)));
    }

    private static IEnumerable<string[]> ReadTable(string path, string header)
    {
        using var rows = File.ReadLines(path).GetEnumerator();
        if (!rows.MoveNext() || rows.Current.TrimStart('\uFEFF') != header)
            throw new InvalidDataException("Invalid TSV header: " + path);
        int count = header.Split('\t').Length;
        while (rows.MoveNext())
        {
            if (string.IsNullOrWhiteSpace(rows.Current)) continue;
            var c = rows.Current.Split('\t');
            if (c.Length != count || c.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("Invalid TSV row: " + path + " / " + rows.Current);
            yield return c;
        }
    }

    private static NationSubtitleLine LoadLine(string key, string speaker, string original, string chinese, string path)
    {
        string status = "pending";
        TimeSpan duration = TimeSpan.Zero;
        if (File.Exists(path))
        {
            try
            {
                using var wav = new WaveFileReader(path);
                var f = wav.WaveFormat;
                if (f.Encoding != WaveFormatEncoding.Pcm || f.SampleRate != 48000 || f.Channels != 1 ||
                    f.BitsPerSample != 16 || wav.TotalTime < TimeSpan.FromMilliseconds(100) ||
                    wav.Length % f.BlockAlign != 0)
                    throw new InvalidDataException("Expected non-empty 48 kHz mono 16-bit PCM WAV");
                long bytes = 0;
                byte[] buffer = new byte[8192];
                int read;
                while ((read = wav.Read(buffer, 0, buffer.Length)) > 0) bytes += read;
                if (bytes != wav.Length) throw new InvalidDataException("Truncated WAV data");
                duration = wav.TotalTime;
                status = "ready";
            }
            catch (Exception e) when (e is IOException or ArgumentException or FormatException or InvalidOperationException)
            {
                status = "invalid: " + e.Message;
                Console.WriteLine($"{Path.GetFileName(path)}: {status}; using subtitles");
            }
        }
        return new(key, speaker, original, chinese, path, duration, status);
    }

    public bool Supports(string category) => UnitCategories.Contains(category);
    public int PoolSize(string category, string trigger) => pools[category + "/" + trigger].Count;
    public NationSubtitleLine Pick(string category, string trigger, string? resource = null)
    {
        if (category == "ORDER")
        {
            string key = NationCardCatalog.Normalize(resource);
            return orders.TryGetValue(key, out var order) ? order :
                throw new InvalidDataException("Unmapped " + Definition.Id + " order: " + resource);
        }
        string eventKey = category + "/" + trigger;
        if (pools.TryGetValue(eventKey, out var pool)) return pool.Next();
        return exact.TryGetValue(eventKey, out var line) ? line :
            throw new InvalidDataException("Unmapped " + Definition.Id + " event: " + eventKey);
    }

    private sealed class LinePool(NationSubtitleLine[] lines)
    {
        private readonly int[] order = Enumerable.Range(0, lines.Length).ToArray();
        private int position = lines.Length;
        private int previous = -1;
        public int Count => lines.Length;
        public NationSubtitleLine Next()
        {
            lock (order)
            {
                if (position == order.Length)
                {
                    Random.Shared.Shuffle(order);
                    if (order.Length > 1 && order[0] == previous)
                        (order[0], order[1]) = (order[1], order[0]);
                    position = 0;
                }
                previous = order[position++];
                return lines[previous];
            }
        }
    }
}
