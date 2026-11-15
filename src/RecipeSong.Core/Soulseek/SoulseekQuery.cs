using System.Text.RegularExpressions;

namespace RecipeSong.Core.Soulseek;

/// <summary>앨범 받기 창의 처음 검색어 (사용자가 고쳐 쓸 수 있다)</summary>
public static partial class SoulseekQuery
{
    /// <summary>
    /// "아티스트 앨범" 검색어. 아포스트로피가 든 단어(Don't, N')는 통째로 뺀다.
    /// 예전처럼 아포스트로피를 공백으로 바꾸면 "Don t" 가 되는데, 한 글자 단어는 상대 클라이언트마다 다르게 다뤄지고
    /// 공유 파일은 Don't · Don’t · Dont 로 제각각이라 단어 단위로 맞추는 클라이언트에서는 "Don" 으로도 "Dont" 가 안 걸린다.
    /// 빼면 결과가 넓어지지만 창이 앨범 곡수로 한 번 더 거른다. 다 빼서 비면 아포스트로피만 지운 꼴(Dont)로 둔다.
    /// &amp; · 대시는 예전처럼 공백으로 바꾼다. 겹치는 단어는 한 번만 둔다 (셀프 타이틀 "Megasound Megasound" → "Megasound").
    /// 협업 프로젝트 앨범은 카드 아티스트 대신 Deezer 주 아티스트로 다시 부른다 (SoulseekSearchView).
    /// </summary>
    public static string ForAlbum(string artist, string album) =>
        string.Join(' ', Clean($"{Part(artist)} {Part(album)}").Split(' ').Distinct(StringComparer.OrdinalIgnoreCase));

    private static string Part(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = words.Where(w => !Apostrophe().IsMatch(w)).ToArray();
        return kept.Any(w => Clean(w).Length > 0) ? string.Join(' ', kept) : Apostrophe().Replace(text, "");
    }

    private static string Clean(string text) => Spaces().Replace(Separators().Replace(text, " "), " ").Trim();

    [GeneratedRegex(@"['`‘’ʼ＇]")]
    private static partial Regex Apostrophe();

    [GeneratedRegex(@"[&'`‘’ʼ＇\p{Pd}]+")]
    private static partial Regex Separators();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
