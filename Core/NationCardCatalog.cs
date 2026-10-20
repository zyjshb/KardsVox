namespace KardsVox.Audio;

internal sealed class NationCardCatalog
{
    private readonly Dictionary<string, string> categories = new(StringComparer.OrdinalIgnoreCase);

    public int Count => categories.Count;

    public NationCardCatalog(string runtimeRoot, string nation, int expectedCount)
    {
        string path = Path.Combine(runtimeRoot, "Data", nation, "card_catalog.tsv");
        if (!File.Exists(path)) throw new FileNotFoundException($"{nation} card catalog is missing", path);
        foreach (string row in File.ReadLines(path).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(row)) continue;
            string[] columns = row.Split('\t');
            if (columns.Length != 2 || !columns[0].StartsWith("card_", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Invalid {nation} card catalog row: {row}");
            if (columns[1] is not ("INFANTRY" or "TANK" or "AIR" or "NAVY" or "ORDER" or "LOCATION"))
                throw new InvalidDataException($"Invalid {nation} card category: {row}");
            if (!categories.TryAdd(columns[0], columns[1]))
                throw new InvalidDataException($"Duplicate {nation} card catalog row: {columns[0]}");
        }
        if (categories.Count != expectedCount)
            throw new InvalidDataException($"Incomplete {nation} card catalog: {categories.Count}/{expectedCount} resources");
    }

    public string? Classify(params string?[] names)
    {
        foreach (string? value in names)
        {
            string normalized = Normalize(value);
            if (normalized.Length > 0 && categories.TryGetValue(normalized, out string? category))
                return category == "LOCATION" ? null : category;
        }
        return null;
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
