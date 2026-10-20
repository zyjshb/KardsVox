using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace KardsVox.Audio;

internal sealed class OrderVoiceProfiles
{
	private readonly OrderVoiceProfile[] profiles;

	public OrderVoiceProfiles(string runtimeRoot)
	{
		string path = Path.Combine(runtimeRoot, "Data", "Japan", "order_profiles.json");
		if (!File.Exists(path))
		{
			throw new FileNotFoundException("Japan order profile list is missing", path);
		}
		profiles = JsonSerializer.Deserialize<OrderVoiceProfile[]>(File.ReadAllText(path), new JsonSerializerOptions
		{
			PropertyNameCaseInsensitive = true
		}) ?? Array.Empty<OrderVoiceProfile>();
		if (profiles.Length == 0 || profiles.Any((OrderVoiceProfile profile) => string.IsNullOrWhiteSpace(profile.Event)))
		{
			throw new InvalidDataException("Japan order profile list is empty or invalid.");
		}
	}

	public OrderVoiceProfile Resolve(params string?[] names)
	{
		string combined = string.Join(' ', names.Where((string value) => !string.IsNullOrWhiteSpace(value))).Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant();
		if (combined.Length == 0)
		{
			return OrderVoiceProfile.Generic;
		}
		OrderVoiceProfile[] array = profiles;
		foreach (OrderVoiceProfile profile in array)
		{
			if (profile.Aliases.Any((string alias) => combined.Contains(alias, StringComparison.OrdinalIgnoreCase)))
			{
				return profile;
			}
		}
		return OrderVoiceProfile.Generic;
	}
}
