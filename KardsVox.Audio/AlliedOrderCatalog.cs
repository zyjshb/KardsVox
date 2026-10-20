using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KardsVox.Audio;

internal sealed record AlliedOrderLabel(string Resource, string Origin, string JapaneseTitle,
    string RussianTitle, string ChineseTitle, string ChineseEffect);

// Read-only labels exported from the game's own current localization files.
// An allied order played in a Japan/Soviet deck remains identifiable without
// pretending that it belongs to either country's custom order dialogue pack.
internal sealed class AlliedOrderCatalog
{
    private readonly Dictionary<string, AlliedOrderLabel> byResource = new(StringComparer.OrdinalIgnoreCase);

    public int Count => byResource.Count;

    public AlliedOrderCatalog(string runtimeRoot)
    {
        string path = Path.Combine(runtimeRoot, "Data", "Allied", "order_labels.tsv");
        if (!File.Exists(path)) throw new FileNotFoundException("Allied order labels are missing", path);
        foreach (string row in File.ReadLines(path).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(row)) continue;
            string[] columns = row.Split('\t');
            if (columns.Length != 6 || columns.Take(5).Any(string.IsNullOrWhiteSpace) ||
                !columns[0].StartsWith("card_event_", StringComparison.OrdinalIgnoreCase) ||
                !byResource.TryAdd(columns[0], new AlliedOrderLabel(columns[0], columns[1],
                    columns[2], columns[3], columns[4], columns[5])))
                throw new InvalidDataException("Invalid or duplicate allied order label: " + row);
        }
        if (byResource.Count != 510 || !byResource.ContainsKey("card_event_unity_is_strength"))
            throw new InvalidDataException($"Incomplete allied order labels: {byResource.Count}/510");
    }

    public bool TryGet(out AlliedOrderLabel label, params string?[] names)
    {
        foreach (string? name in names)
            if (byResource.TryGetValue(Normalize(name), out label!)) return true;
        label = null!;
        return false;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string text = value.ToLowerInvariant();
        int start = text.IndexOf("card_event_", StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        int end = start;
        while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] == '_')) end++;
        string result = text[start..end];
        return result.EndsWith("_c", StringComparison.Ordinal) ? result[..^2] : result;
    }
}
