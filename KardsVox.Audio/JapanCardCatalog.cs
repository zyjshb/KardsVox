using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KardsVox.Audio;

internal sealed class JapanCardCatalog
{
	private readonly Dictionary<string, string> categories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	public int Count => categories.Count;

	public JapanCardCatalog(string runtimeRoot)
	{
		string path = Path.Combine(runtimeRoot, "Data", "Japan", "card_catalog.tsv");
		if (!File.Exists(path))
		{
			throw new FileNotFoundException("Japan card catalog is missing", path);
		}
		foreach (string row in File.ReadLines(path).Skip(1))
		{
			if (!string.IsNullOrWhiteSpace(row))
			{
				string[] columns = row.Split('\t');
				if (columns.Length != 2 || !columns[0].StartsWith("card_", StringComparison.OrdinalIgnoreCase))
				{
					throw new InvalidDataException("Invalid Japan card catalog row: " + row);
				}
				categories[columns[0]] = columns[1];
			}
		}
		if (categories.Count < 300)
		{
			throw new InvalidDataException($"Incomplete Japan card catalog: {categories.Count}/312 resources");
		}
	}

	public string? Classify(params string?[] names)
	{
		for (int i = 0; i < names.Length; i++)
		{
			string normalized = Normalize(names[i]);
			if (normalized.Length > 0 && categories.TryGetValue(normalized, out string category))
			{
				if (!(category == "LOCATION"))
				{
					return category;
				}
				return null;
			}
		}
		return null;
	}

	private static string Normalize(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return string.Empty;
		}
		string text = value.ToLowerInvariant();
		int start = text.IndexOf("card_", StringComparison.Ordinal);
		if (start < 0)
		{
			return string.Empty;
		}
		int end;
		for (end = start; end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] == '_'); end++)
		{
		}
		int num = start;
		string result = text.Substring(num, end - num);
		if (!result.EndsWith("_c", StringComparison.Ordinal))
		{
			return result;
		}
		string text2 = result;
		return text2.Substring(0, text2.Length - 2);
	}
}
