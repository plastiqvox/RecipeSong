using System.Text.RegularExpressions;

namespace RecipeSong.Core;

/// <summary>같은 앨범의 다른 버전 알아보기: "Album (Instrumental)", "Album - Instrumentals", "Album (Inst.)", "Album Instrumental Ver."</summary>
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

    public static bool SameTitle(string a, string b) =>
        ListeningAnalyzer.NormalizeArtist(a) == ListeningAnalyzer.NormalizeArtist(b);
}
