namespace KardsVox.Audio;

internal sealed record JapanOrderSubtitle(string Resource, string Title, string Line, string Japanese);

internal sealed class JapanOrderSubtitleCatalog
{
    private readonly Dictionary<string, JapanOrderSubtitle> entries = new(StringComparer.OrdinalIgnoreCase);

    public int Count => entries.Count;

    public JapanOrderSubtitleCatalog(string runtimeRoot)
    {
        string path = Path.Combine(runtimeRoot, "Data", "Japan", "order_lines.tsv");
        string originalsPath = Path.Combine(runtimeRoot, "Data", "Japan", "order_originals.tsv");
        if (!File.Exists(path)) throw new FileNotFoundException("Japan order subtitle catalog is missing", path);
        if (!File.Exists(originalsPath)) throw new FileNotFoundException("Japan order original lines are missing", originalsPath);
        Dictionary<string, string> originals = new(StringComparer.OrdinalIgnoreCase);
        foreach (string row in File.ReadLines(originalsPath).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(row)) continue;
            string[] columns = row.Split('\t');
            if (columns.Length != 2 || columns.Any(string.IsNullOrWhiteSpace) ||
                !originals.TryAdd(Normalize(columns[0]), columns[1]))
                throw new InvalidDataException("Invalid or duplicate Japan order original: " + row);
        }
        foreach (string row in File.ReadLines(path).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(row)) continue;
            string[] columns = row.Split('\t');
            if (columns.Length != 3 || columns.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("Invalid Japan order subtitle row: " + row);
            string key = Normalize(columns[0]);
            if (!originals.TryGetValue(key, out string? japanese))
                throw new InvalidDataException("Missing Japanese order line: " + columns[0]);
            if (!entries.TryAdd(key, new JapanOrderSubtitle(columns[0], columns[1], columns[2], japanese)))
                throw new InvalidDataException("Duplicate Japan order subtitle: " + columns[0]);
        }
        if (entries.Count != 124 || originals.Count != entries.Count)
            throw new InvalidDataException($"Incomplete Japan order subtitle catalog: {entries.Count}/{originals.Count}/124");
    }

    public JapanOrderSubtitle Resolve(params string?[] names)
    {
        foreach (string? name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            string normalized = Normalize(name);
            if (entries.TryGetValue(normalized, out JapanOrderSubtitle? exact)) return exact;
            foreach (KeyValuePair<string, JapanOrderSubtitle> entry in entries)
                if (normalized.Contains(entry.Key, StringComparison.OrdinalIgnoreCase)) return entry.Value;
        }
        return new JapanOrderSubtitle("unknown", "前线急电", "命令已送达，各部立即依令行动。",
            "命令を伝達した。各隊は直ちに行動せよ。");
    }

    private static string Normalize(string value)
    {
        string normalized = value.Trim().ToLowerInvariant();
        if (normalized.EndsWith("_c", StringComparison.Ordinal)) normalized = normalized[..^2];
        return normalized;
    }
}
