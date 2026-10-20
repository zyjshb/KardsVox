using System.Text.Json;
using System.Text.RegularExpressions;

namespace KardsVox.Core;

internal sealed record VoicePackDefinition(
    string Id, string Name, string EnglishName, string Description, int CardCount, bool Playable,
    string Language = "", string AudioPrefix = "", bool FileBased = false);

internal static class VoicePackRegistry
{
    public const string Japan = "Japan";
    public const string Soviet = "Soviet";
    public const string Germany = "Germany";
    public const string Finland = "Finland";

    public static readonly IReadOnlyList<VoicePackDefinition> All = Load();

    private static IReadOnlyList<VoicePackDefinition> Load()
    {
        List<VoicePackDefinition> result =
        [
            new(Japan, "日本", "JAPAN", "日语语音 · 日中字幕", 312, true, "ja", "JP"),
            new(Soviet, "苏联", "SOVIET UNION", "俄语语音 · 俄中字幕", 319, true, "ru", "SU")
        ];
        string data = Path.Combine(AppContext.BaseDirectory, "Data");
        if (!Directory.Exists(data)) return result;
        foreach (string folder in Directory.GetDirectories(data)
                     .OrderBy(folder => Path.GetFileName(folder) == Germany ? 0 : 1).ThenBy(folder => folder))
        {
            string path = Path.Combine(folder, "pack.json");
            if (!File.Exists(path)) continue;
            var pack = JsonSerializer.Deserialize<VoicePackDefinition>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("Invalid pack definition: " + path);
            if (pack.Id != Path.GetFileName(folder) || !Regex.IsMatch(pack.Id, "^[A-Za-z]+$") ||
                !Regex.IsMatch(pack.AudioPrefix, "^[A-Z]{2,4}$") || !pack.FileBased || pack.CardCount <= 0 ||
                string.IsNullOrWhiteSpace(pack.Name) || string.IsNullOrWhiteSpace(pack.Language) ||
                result.Any(p => p.Id.Equals(pack.Id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Invalid or duplicate pack definition: " + path);
            result.Add(pack);
        }
        return result;
    }

    public static bool Contains(string? id) => All.Any(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static VoicePackDefinition Get(string? id) =>
        All.FirstOrDefault(pack => pack.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public static string NationName(string? id) => id switch
    {
        Japan => "日本",
        Soviet => "苏联",
        "USA" => "美国",
        "Britain" => "英国",
        "Germany" => "德国",
        "Italy" => "意大利",
        "France" => "法国",
        "Poland" => "波兰",
        "Finland" => "芬兰",
        "ANZAC" => "澳新军团",
        "Neutral" => "中立",
        _ => "未知国家"
    };
}
