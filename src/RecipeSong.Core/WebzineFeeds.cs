using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RecipeSong.Core;

/// <summary>웹진 RSS 하나. Parser는 제목 형식 (사이트마다 다르다). Region은 crates.json 지역 이름.</summary>
public sealed record FeedSource(string Name, string Url, string Region, string Parser);

/// <summary>
/// 외부 추천 가져오기: 웹진 RSS의 리뷰·추천 글 제목에서 "아티스트 – 앨범"을 뽑는다.
/// 뽑은 건 AiPick 모양으로 돌려주고, Deezer 확인·소리 점수는 ReleaseRecommender.ScoreAiPicksAsync가 한다.
/// </summary>
public sealed class WebzineFeeds(HttpClient http)
{
    /// <summary>기본 웹진 (RSS로 읽을 수 있는 것만 — 2026-10 확인)</summary>
    public static readonly FeedSource[] Defaults =
    [
        new("Pitchfork", "https://pitchfork.com/feed/feed-album-reviews/rss", "해외", "pitchfork"),
        new("The Quietus", "https://thequietus.com/feed", "해외", "quietus"),
        new("Aquarium Drunkard", "https://aquariumdrunkard.com/feed/", "해외", "colons"),
        new("Bandcamp Daily", "https://daily.bandcamp.com/feed", "해외", "bandcamp"),
        new("Stereogum", "https://www.stereogum.com/feed/", "해외", "stereogum"),
    ];

    /// <summary>
    /// 피드는 있지만 프로그램 접근을 막아 둔 곳 (2026-10 확인: weiv는 403). 사이트가 허용하면 Defaults로 옮긴다.
    /// </summary>
    public static readonly FeedSource[] Blocked =
    [
        new("weiv", "https://www.weiv.co.kr/feed", "한국", "weiv"),
    ];

    public sealed record Item(string Title, string Link, DateTime? Published);

    /// <summary>피드를 읽어 앨범 후보로. 형식에 안 맞는 글(인터뷰·뉴스 등)은 건너뛴다.</summary>
    public async Task<IReadOnlyList<AiPick>> ReadAsync(FeedSource feed, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, feed.Url);
        // 앱 이름을 그대로 밝힌다. 프로그램 접근을 막은 사이트는 우회하지 않는다.
        req.Headers.UserAgent.ParseAdd("RecipeSong/0.1 (personal music discovery desktop app)");
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var xml = XDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var items = xml.Descendants("item").Select(i => new Item(
            WebUtility.HtmlDecode((string?)i.Element("title") ?? "").Trim(),
            ((string?)i.Element("link") ?? "").Trim(),
            DateTime.TryParse((string?)i.Element("pubDate"), out var d) ? d : null)).ToList();
        return [.. items.Select(i => Parse(feed, i)).OfType<AiPick>()];
    }

    private static readonly Regex Dash = new(@"^(.+?)\s+[–—-]\s+(.+)$");
    private static readonly Regex Year = new(@"\s*\((?:19|20)\d{2}\)\s*$");
    private static readonly Regex Paren = new(@"\s*\(([^)]*)\)\s*");
    private static readonly Regex Announce = new(@"^(.+?) Announces? (?:(?:New|Debut|First|Their|His|Her|Sophomore|Solo|Surprise|Collaborative|Joint|Live|Covers|Final|Long-Awaited|Self-Titled|Another|Second|Third|Fourth|Fifth)[\w-]*\s+)*(?:Album|LP|EP)\s+(.+?)(?::|,| \(| —| Out (?:Now|Today|Tomorrow|Friday|Next|This|In|On)\b|$)", RegexOptions.IgnoreCase);
    private static readonly Regex QuotedTitle = new("^(.+?),\\s*[“\"](.+?)[”\"]\\s*$");

    private static AiPick? Parse(FeedSource feed, Item item)
    {
        (string Artist, string Album)? got = feed.Parser switch
        {
            "pitchfork" => Pitchfork(item),
            "quietus" => item.Link.Contains("/quietus-reviews/") ? Split(Dash, item.Title) : null,
            "colons" => item.Title.Split(" :: ") is [var a, var b] && !Regex.IsMatch(b, "Interview|Mix|Playlist|Videodrome", RegexOptions.IgnoreCase)
                        && !Regex.IsMatch(a, "Videodrome|Transmissions|Lagniappe", RegexOptions.IgnoreCase) ? (a.Trim(), b.Trim()) : null,
            "bandcamp" => item.Link.Contains("/album-of-the-day/") ? Split(QuotedTitle, item.Title) : null,
            "stereogum" => Announce.Match(item.Title) is { Success: true } m ? (m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim()) : null,
            "weiv" => Weiv(item.Title),
            _ => Split(Dash, item.Title),
        };
        if (got is not { } g || g.Artist.Length == 0 || g.Album.Length == 0) return null;
        return new AiPick(g.Artist, g.Album, item.Published?.ToString("yyyy-MM-dd"), null, feed.Region,
            $"{feed.Name}에서 소개", item.Link, $"웹진 · {feed.Name}");
    }

    private static (string, string)? Split(Regex re, string title) =>
        re.Match(title) is { Success: true } m ? (m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim()) : null;

    /// <summary>Pitchfork: 제목은 앨범명뿐 → 링크 "reviews/albums/{아티스트}-{앨범}/"에서 앨범 부분을 떼면 아티스트</summary>
    private static (string, string)? Pitchfork(Item item)
    {
        var m = Regex.Match(item.Link, @"/reviews/albums/([^/]+)/?");
        if (!m.Success) return null;
        var slug = m.Groups[1].Value;
        var albumSlug = Regex.Replace(item.Title.ToLowerInvariant().Replace("&", "and"), @"[^a-z0-9]+", "-").Trim('-');
        if (albumSlug.Length == 0 || !slug.EndsWith(albumSlug) || slug.Length <= albumSlug.Length + 1) return null;
        var artist = slug[..^(albumSlug.Length + 1)].Replace('-', ' ');
        return (artist, item.Title);
    }

    /// <summary>weiv: "칩 포스트 갱(Chip Post Gang) – 앨범 (2026)" → 영문 이름이 있으면 그걸로 (Deezer 검색이 잘 된다)</summary>
    private static (string, string)? Weiv(string title)
    {
        if (title.Contains(" | ") || Split(Dash, title) is not { } parts) return null;  // " | "는 인터뷰·기획 글
        var (artist, album) = parts;
        album = Year.Replace(album, "");
        var inner = Paren.Match(artist);
        if (inner.Success && Regex.IsMatch(inner.Groups[1].Value, "[A-Za-z]")) artist = inner.Groups[1].Value;
        else artist = Paren.Replace(artist, " ").Trim();
        return (artist.Trim(), album.Trim());
    }
}
