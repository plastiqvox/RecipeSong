using System.Text.Json;
using System.Text.Json.Serialization;

namespace RecipeSong.Core;

/// <summary>
/// iTunes 플레이리스트 → 레시피(스타일) / 지역 매핑. Python 학습과 같은 파일을 쓴다.
/// 앱의 레시피 설정 창이 ml/data/crates.json 에 쓰고, 그게 없으면 저장소의 ml/crates.json (만든 사람의 플레이리스트).
/// </summary>
public sealed class CrateConfig
{
    /// <summary>레시피 설정 창에서 고를 수 있는 지역 (추천 엔진의 Region 과 같은 이름)</summary>
    public static readonly string[] Regions = ["일본", "한국", "해외", "동남아"];

    /// <summary>Region 이 null 이면 '섞임': 지역을 학습하지 않는다 (여러 나라가 섞인 플레이리스트)</summary>
    public sealed record Entry(
        [property: JsonPropertyName("style")] string? Style,
        [property: JsonPropertyName("region")] string? Region);

    [JsonPropertyName("playlists")] public Dictionary<string, Entry> Playlists { get; init; } = [];
    [JsonPropertyName("ignore")] public List<string> Ignore { get; init; } = [];

    public static string UserPath(string mlDirectory) => Path.Combine(mlDirectory, "data", "crates.json");

    public static CrateConfig Load(string mlDirectory)
    {
        var user = UserPath(mlDirectory);
        var path = File.Exists(user) ? user : Path.Combine(mlDirectory, "crates.json");
        return File.Exists(path)
            ? JsonSerializer.Deserialize<CrateConfig>(File.ReadAllText(path)) ?? new CrateConfig()
            : new CrateConfig();
    }

    public void Save(string mlDirectory)
    {
        var path = UserPath(mlDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions
            { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    /// <summary>이 라이브러리에 있는 레시피 플레이리스트 수 (0이면 다른 사람 라이브러리 → 레시피 설정 창을 띄운다)</summary>
    public int Matching(IEnumerable<string> playlistNames) => playlistNames.Count(Uses);

    /// <summary>해외 플레이리스트에 레시피 이름을 하나도 안 붙였으면 해외 발매작은 Deezer 장르로 나눈다 (만든 사람의 방식)</summary>
    [JsonIgnore] public bool SplitsOverseasByGenre => RegionWithoutStyle("해외");
    /// <summary>한국 플레이리스트에 레시피 이름을 안 붙였으면 한국 발매작은 '한국음악' 하나로</summary>
    [JsonIgnore] public bool KoreanAsOneRecipe => RegionWithoutStyle("한국");

    private bool RegionWithoutStyle(string region)
    {
        var entries = Playlists.Where(kv => !Ignore.Contains(kv.Key) && kv.Value.Region == region).Select(kv => kv.Value).ToList();
        return entries.Count > 0 && entries.All(e => e.Style == null);
    }

    public bool Uses(string playlist) => Playlists.ContainsKey(playlist) && !Ignore.Contains(playlist);
}
