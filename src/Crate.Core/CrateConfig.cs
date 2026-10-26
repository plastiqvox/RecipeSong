using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crate.Core;

/// <summary>ml/crates.json: iTunes 플레이리스트 → 크레이트(스타일) / 지역 매핑. Python 학습과 같은 파일을 쓴다.</summary>
public sealed class CrateConfig
{
    public sealed record Entry(
        [property: JsonPropertyName("style")] string? Style,
        [property: JsonPropertyName("region")] string Region);

    [JsonPropertyName("playlists")] public Dictionary<string, Entry> Playlists { get; init; } = [];
    [JsonPropertyName("ignore")] public List<string> Ignore { get; init; } = [];

    public static CrateConfig Load(string mlDirectory)
    {
        var path = Path.Combine(mlDirectory, "crates.json");
        return File.Exists(path)
            ? JsonSerializer.Deserialize<CrateConfig>(File.ReadAllText(path)) ?? new CrateConfig()
            : new CrateConfig();
    }

    public bool Uses(string playlist) => Playlists.ContainsKey(playlist) && !Ignore.Contains(playlist);
}
