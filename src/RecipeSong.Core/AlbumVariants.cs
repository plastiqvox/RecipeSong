using System.Text.RegularExpressions;

namespace RecipeSong.Core;

/// <summary>같은 앨범의 다른 버전 알아보기: "Album (Instrumental)", "Album - Instrumentals", "Album (Inst.)", "Album Instrumental Ver.", 라이브판 · 리믹스</summary>
public static partial class AlbumVariants
{
    [GeneratedRegex(@"(?:\s+|\s*[\(\[]\s*|\s*[-–—:]\s*)(?:the\s+)?(?:instrumentals?|inst\.?)(?:\s+(?:version|versions|edition|ver\.?|tracks))?\s*[\)\]]?\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex Instrumental();

    /// <summary>인스트루멘털 버전이면 원래 앨범 제목, 아니면 null ("Instrumentals"처럼 제목 전체가 그 말이면 null)</summary>
    public static string? InstrumentalBase(string title)
    {
        var m = Instrumental().Match(title);
        if (!m.Success || m.Index == 0) return null;
        var baseTitle = title[..m.Index].Trim();
        return baseTitle.Length > 0 ? baseTitle : null;
    }

    // 라이브판: 괄호 안의 live ("(Live in Paris)"), 대시·쌍점 뒤 ("Hidden in Pieces: Live at …"), "Live at/from/session/selection …", "Live 2022", ライブ · 라이브.
    // 괄호 밖의 "Live in" · 끝의 "Live" 는 곡 제목("Still Live In That Summer", "Let Me Live")과 헷갈려 보지 않는다
    [GeneratedRegex(@"[\(\[][^\)\]]*\blive\b[^\)\]]*[\)\]]|(?:\s[-–—]|:)\s*(?:[^-–—:]*\s)?live\b|\blive\s+(?:at|from|session|sessions|selection|recording|recordings|tour)\b|\blive\s+(?:19|20)\d{2}\b|ライブ|라이브",
        RegexOptions.IgnoreCase)]
    private static partial Regex Live();

    [GeneratedRegex(@"\b(?:remix(?:es|ed)?|rmx)\b|リミックス|리믹스", RegexOptions.IgnoreCase)]
    private static partial Regex Remix();

    /// <summary>라이브 공연 녹음 (제목으로 본다)</summary>
    public static bool IsLive(string title) => Live().IsMatch(title);

    /// <summary>리믹스 · 리믹스 모음 (제목으로 본다)</summary>
    public static bool IsRemix(string title) => Remix().IsMatch(title);

    public static bool SameTitle(string a, string b) =>
        ListeningAnalyzer.NormalizeArtist(a) == ListeningAnalyzer.NormalizeArtist(b);
}
