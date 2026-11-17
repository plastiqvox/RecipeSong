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
    /// 악센트·합자가 든 라틴 단어(CŒUR, Beyoncé, Mø, Straße)도 같은 이유로 뺀다: 공유 폴더는 CŒUR · Cœur · Coeur 처럼 제각각이고
    /// 상대 클라이언트는 악센트를 접지 않아 "CŒUR" 로는 "Coeur" 가 안 걸린다. 아티스트·앨범 중 한쪽이 다 빠지면 그쪽은
    /// 악센트를 뗀 꼴(Beyoncé → Beyonce)로 둔다 — 통째로 빼면 결과가 너무 넓고, 공유 폴더도 ASCII 로 쓴 경우가 많다.
    /// 한글·일본어 같은 비라틴 글자는 표기가 갈리지 않으므로 그대로 둔다.
    /// &amp; · 대시는 예전처럼 공백으로 바꾼다. 겹치는 단어는 한 번만 둔다 (셀프 타이틀 "Megasound Megasound" → "Megasound").
    /// 협업 프로젝트 앨범은 카드 아티스트 대신 Deezer 주 아티스트로 다시 부른다 (SoulseekSearchView).
    /// </summary>
    public static string ForAlbum(string artist, string album) =>
        string.Join(' ', Clean($"{Part(artist)} {Part(album)}").Split(' ').Distinct(StringComparer.OrdinalIgnoreCase));

    private static string Part(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = words.Where(w => !Apostrophe().IsMatch(w) && !HasVariantLatin(w)).ToArray();
        return kept.Any(w => Clean(w).Length > 0) ? string.Join(' ', kept) : Fold(Apostrophe().Replace(text, ""));
    }

    /// <summary>표기가 갈리는 라틴 글자 (악센트·합자: À–ɏ, Ḁ–ỿ)가 들어 있다</summary>
    private static bool HasVariantLatin(string word) => word.Any(c => c is >= 'À' and <= 'ɏ' or >= 'Ḁ' and <= 'ỿ');

    /// <summary>악센트를 떼고 합자를 푼다 (CŒUR → COEUR, Beyoncé → Beyonce, Straße → Strasse)</summary>
    private static string Fold(string text)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in text.Normalize(System.Text.NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            sb.Append(c switch
            {
                'Œ' => "OE", 'œ' => "oe", 'Æ' => "AE", 'æ' => "ae", 'ß' => "ss", 'Ø' => "O", 'ø' => "o",
                'Đ' => "D", 'đ' => "d", 'Ł' => "L", 'ł' => "l", 'Þ' => "Th", 'þ' => "th", 'Ð' => "D", 'ð' => "d", 'ı' => "i",
                _ => c.ToString(),
            });
        }
        return sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
    }

    private static string Clean(string text) => Spaces().Replace(Separators().Replace(text, " "), " ").Trim();

    [GeneratedRegex(@"['`‘’ʼ＇]")]
    private static partial Regex Apostrophe();

    [GeneratedRegex(@"[&'`‘’ʼ＇\p{Pd}]+")]
    private static partial Regex Separators();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
