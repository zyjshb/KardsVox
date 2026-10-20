using System.Text.Json;

namespace KardsVox.Audio;

internal sealed record OrderVoiceProfile(string Name, string Event, string Telegraph, string[] Aliases)
{
    public static readonly OrderVoiceProfile Generic =
        new("作战命令", "ORDER_ANY", ".. .. ..", []);
}

internal sealed class OrderVoiceProfiles
{
    private readonly OrderVoiceProfile[] profiles;

    public OrderVoiceProfiles(string runtimeRoot)
    {
        string path = Path.Combine(runtimeRoot, "Data", "Japan", "order_profiles.json");
        if (!File.Exists(path)) throw new FileNotFoundException("Japan order profile list is missing", path);
        profiles = JsonSerializer.Deserialize<OrderVoiceProfile[]>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? [];
        if (profiles.Length == 0 || profiles.Any(profile => string.IsNullOrWhiteSpace(profile.Event)))
            throw new InvalidDataException("Japan order profile list is empty or invalid.");
    }

    public OrderVoiceProfile Resolve(params string?[] names)
    {
        string combined = string.Join(' ', names.Where(value => !string.IsNullOrWhiteSpace(value)))
            .Replace("-", "_", StringComparison.Ordinal)
            .ToLowerInvariant();
        if (combined.Length == 0) return OrderVoiceProfile.Generic;

        foreach (var profile in profiles)
            if (profile.Aliases.Any(alias => combined.Contains(alias, StringComparison.OrdinalIgnoreCase)))
                return profile;
        return OrderVoiceProfile.Generic;
    }
}
