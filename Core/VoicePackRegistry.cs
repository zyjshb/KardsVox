namespace KardsVox.Core;

internal sealed record VoicePackDefinition(
    string Id, string Name, string EnglishName, string Description, int CardCount, bool Playable);

internal static class VoicePackRegistry
{
    public const string Japan = "Japan";
    public const string Soviet = "Soviet";

    public static readonly IReadOnlyList<VoicePackDefinition> All =
    [
        new(Japan, "日本", "JAPAN", "已收录 144 条语音 · 开场、出牌、战况提示", 312, true),
        new(Soviet, "苏联", "SOVIET UNION", "卡牌目录已建立 · 等待苏联语音素材", 319, false)
    ];

    public static VoicePackDefinition Get(string? id) =>
        All.FirstOrDefault(pack => pack.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? All[0];
}
