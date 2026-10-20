namespace KardsVox.Audio;

internal sealed record AlliedCardLabel(string Resource, string Origin, string Kind,
    string OriginalTitle, string ChineseTitle);

// Nation and card labels exported from the current game's localizations.
// Unsupported nations get a card-name subtitle, never Japan/Soviet speech.
internal sealed class AlliedCardCatalog
{
    private readonly Dictionary<string, AlliedCardLabel> byResource = new(StringComparer.OrdinalIgnoreCase);

    public int Count => byResource.Count;

    public AlliedCardCatalog(string runtimeRoot)
    {
        string path = Path.Combine(runtimeRoot, "Data", "Allied", "card_labels.tsv");
        if (!File.Exists(path)) throw new FileNotFoundException("Allied card labels are missing", path);
        foreach (string row in File.ReadLines(path).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(row)) continue;
            string[] columns = row.Split('\t');
            if (columns.Length != 5 || columns.Take(3).Any(string.IsNullOrWhiteSpace) ||
                !columns[0].StartsWith("card_", StringComparison.OrdinalIgnoreCase) ||
                columns[2] is not ("EVENT" or "UNIT" or "LOCATION") ||
                !byResource.TryAdd(columns[0], new AlliedCardLabel(columns[0], columns[1],
                    columns[2], columns[3], columns[4])))
                throw new InvalidDataException("Invalid or duplicate allied card label: " + row);
        }
        if (byResource.Count != 1309)
            throw new InvalidDataException($"Incomplete allied card labels: {byResource.Count}/1309");
    }

    public bool TryGet(out AlliedCardLabel label, params string?[] names)
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
        int start = text.IndexOf("card_", StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        int end = start;
        while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] == '_')) end++;
        string result = text[start..end];
        return result.EndsWith("_c", StringComparison.Ordinal) ? result[..^2] : result;
    }
}
