using System.Text.Json;

namespace RecipeSong.Core.Ai;

/// <summary>AI 커넥터 하나 (Claude, GPT). 웹을 검색해 신보 후보를 찾는다 — 확인과 소리 판정은 ReleaseRecommender가 한다.</summary>
public interface IAiScout
{
    /// <summary>카드 배지에 쓰는 이름</summary>
    string Name { get; }
    bool IsReady { get; }
    /// <summary>키와 모델이 맞는지 짧은 요청으로 확인한다. 실패하면 이유를 돌려준다.</summary>
    Task<string?> TestAsync(CancellationToken ct = default);
    /// <summary>키가 아직 유효한지만 확인한다 (모델 목록 조회 — 요금 없음). 유효하면 null, 아니면 이유.</summary>
    Task<string?> CheckKeyAsync(CancellationToken ct = default);
    /// <param name="taste">많이 듣는 뮤지션·앱 신호 요약 (프롬프트에 그대로 들어간다)</param>
    /// <param name="exclude">AI 커넥터가 이 기간에 이미 제안한 "아티스트 – 앨범" (다시 제안하지 않게). 일반 추천과 겹치는 앨범은 넣지 않는다</param>
    /// <param name="focusRegion">일본/한국/해외/동남아 중 하나면 그 지역만 (앱은 지역을 나누지 않아 null 로 부른다)</param>
    Task<IReadOnlyList<AiPick>> FindAsync(Period period, string taste, IReadOnlyCollection<string> exclude, string? focusRegion,
        IProgress<string>? progress = null, CancellationToken ct = default);
}

/// <summary>
/// 커넥터들이 같이 쓰는 프롬프트와 결과 스키마.
/// 지시는 영어로 쓴다 — 모델이 규칙을 가장 정확히 따르는 언어이고, 비용 차이는 거의 없다(입력 대부분은 검색 결과).
/// 대신 검색 언어를 씬마다 정해 준다: 지시 언어를 따라 한 언어로만 검색하면 일본·한국 인디를 놓친다.
/// 추천 이유는 화면 언어로 받는다.
/// </summary>
internal static class AiPrompt
{
    public const string System = "You are a curator helping an indie music digger. Only suggest releases that actually exist and whose release date you have confirmed in search results.";

    private static string RegionName(string? r) => r switch
    {
        "일본" => "Japan", "한국" => "Korea", "해외" => "International (outside Japan/Korea)", "동남아" => "Southeast Asia",
        null => "Japan, Korea, International and Southeast Asia", _ => r,
    };

    private static string ReasonLanguage => L.Lang == "en" ? "English" : "Korean";

    /// <param name="taste">앱이 만든 취향 요약: 라이브러리에서 많이 듣는 뮤지션 + 좋아요·별로·차단·팔로우·좋아하는 레이블 (영어 머리글)</param>
    public static string Build(Period period, string taste, IReadOnlyCollection<string> exclude, string? focusRegion, string howToSubmit) => $"""
        Period: {period.Start:yyyy-MM-dd} to {period.End:yyyy-MM-dd}
        Regions: {RegionName(focusRegion)}

        {taste}

        Already suggested before (do not suggest these again):
        {(exclude.Count == 0 ? "(none)" : string.Join("\n", exclude.Take(120)))}

        Task: search the web for albums and EPs actually released in this period that fit my taste.
        - Prefer other artists from the same scenes, labels and sound as the listed artists over those artists themselves — especially small labels and indie releases.
        - Lean toward what I liked and the artists I follow; avoid anything that sounds like the "not for me" list; never suggest blocked artists or labels.
        - Search in each scene's own language: Japanese releases in Japanese (e.g. 新譜, リリース, レーベル 新作, タワーレコード), Korean releases in Korean (e.g. 신보, 발매, 앨범), other releases in English.
        - Start with pages that list many releases at once: new-release roundups, reviews, label release pages, Bandcamp new arrivals.
        - Only include release dates you confirmed in search results; leave out anything you could not confirm.
        - Keep artist and album titles in their original script.
        - If there are not enough albums, add EPs and singles to reach at least 8. Do not stop at 2–3.
        - region must be one of: 일본 (Japan), 한국 (Korea), 해외 (International), 동남아 (Southeast Asia).
        - Write each "reason" in {ReasonLanguage}: one sentence saying which of my artists or scenes it is close to.
        {howToSubmit} (8–15 releases)
        """;

    /// <summary>결과 JSON 스키마 (strict: 모든 object에 additionalProperties: false, 모든 필드 required)</summary>
    public static object Schema => new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "releases" },
        properties = new
        {
            releases = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "artist", "album", "release_date", "type", "region", "reason", "source_url" },
                    properties = new Dictionary<string, object>
                    {
                        ["artist"] = new { type = "string" },
                        ["album"] = new { type = "string", description = "Album or EP title in its original script" },
                        ["release_date"] = new { type = "string", description = "YYYY-MM-DD" },
                        ["type"] = new { type = "string", @enum = new[] { "album", "ep", "single" } },
                        ["region"] = new { type = "string", @enum = new[] { "일본", "한국", "해외", "동남아" } },
                        ["reason"] = new { type = "string", description = $"One sentence in {ReasonLanguage}: which crate or artists it is close to" },
                        ["source_url"] = new { type = "string", description = "Page where you confirmed the release date" },
                    },
                },
            },
        },
    };

    public static List<AiPick> Parse(JsonElement releases, string by) =>
        [.. releases.EnumerateArray().Select(r => new AiPick(
            r.GetProperty("artist").GetString() ?? "",
            r.GetProperty("album").GetString() ?? "",
            r.GetProperty("release_date").GetString(),
            r.GetProperty("type").GetString(),
            r.GetProperty("region").GetString(),
            r.GetProperty("reason").GetString() ?? "",
            r.GetProperty("source_url").GetString(),
            by))
            .Where(p => p.Artist.Length > 0 && p.Album.Length > 0)];
}
