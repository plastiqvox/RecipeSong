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
    /// <param name="taste">크레이트별 대표 뮤지션 요약 (프롬프트에 그대로 들어간다)</param>
    /// <param name="exclude">이미 화면에 있는 "아티스트 – 앨범" (다시 제안하지 않게)</param>
    /// <param name="focusRegion">일본/한국/해외/동남아 중 하나면 그 지역만</param>
    Task<IReadOnlyList<AiPick>> FindAsync(Period period, string taste, IReadOnlyCollection<string> exclude, string? focusRegion,
        IProgress<string>? progress = null, CancellationToken ct = default);
}

/// <summary>커넥터들이 같이 쓰는 프롬프트와 결과 스키마</summary>
internal static class AiPrompt
{
    public const string System = "당신은 인디 음악 디거를 돕는 큐레이터입니다. 실제로 존재하고 발매일을 확인한 발매작만 제안합니다.";

    public static string Build(Period period, string taste, IReadOnlyCollection<string> exclude, string? focusRegion, string howToSubmit) => $"""
        기간: {period.Start:yyyy-MM-dd} ~ {period.End:yyyy-MM-dd}
        지역: {focusRegion ?? "일본, 한국, 해외, 동남아 모두"}

        내 크레이트와 대표 뮤지션:
        {taste}

        이미 알고 있는 발매작 (제외):
        {(exclude.Count == 0 ? "(없음)" : string.Join("\n", exclude.Take(120)))}

        이 기간에 실제로 발매된 앨범·EP 중 내 크레이트 취향에 맞을 만한 것을 웹에서 찾아 주세요.
        위 뮤지션 본인보다는 같은 씬·레이블·결의 다른 뮤지션, 특히 작은 레이블과 인디 발매작을 우선해 주세요.
        발매일은 검색 결과로 확인한 것만 넣고, 확인하지 못한 것은 넣지 마세요.
        신보 소식·리뷰 사이트, 레이블 신보 페이지, Bandcamp 신보 목록처럼 한 페이지에 여러 장이 모인 곳을 먼저 찾으면 효율적이에요.
        앨범이 모자라면 EP와 싱글도 넣어서 8장 이상 채워 주세요. 2~3장만 내지 마세요.
        {howToSubmit} (8~15장){(L.Lang == "en" ? "\nWrite each reason (the \"reason\" field) in English." : "")}
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
                        ["album"] = new { type = "string", description = "앨범·EP 제목 (원어 표기)" },
                        ["release_date"] = new { type = "string", description = "YYYY-MM-DD" },
                        ["type"] = new { type = "string", @enum = new[] { "album", "ep", "single" } },
                        ["region"] = new { type = "string", @enum = new[] { "일본", "한국", "해외", "동남아" } },
                        ["reason"] = new { type = "string", description = L.Lang == "en"
                            ? "One-sentence reason for the pick, in English (which crate/artists it is close to)"
                            : "추천 이유 한 문장 (한국어, 어떤 크레이트·뮤지션과 결이 비슷한지)" },
                        ["source_url"] = new { type = "string", description = "발매일을 확인한 페이지" },
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
