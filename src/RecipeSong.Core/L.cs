using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace RecipeSong.Core;

/// <summary>
/// 다국어. 화면 문구는 한국어 원문이 그대로 키다: L.T("추천 설정"), L.F("{0}장", n).
/// 번역은 Lang/en.json (원문 → 번역, 내장 리소스). 번역이 없으면 원문을 그대로 보여 준다.
/// 언어는 시작할 때 한 번 정한다 (바꾸면 다시 시작).
/// </summary>
public static class L
{
    public static readonly (string Code, string Name)[] Languages = [("ko", "한국어"), ("en", "English")];

    public static string Lang { get; private set; } = "ko";
    private static Dictionary<string, string> _map = [];

    /// <summary>언어 코드가 없으면 Windows 표시 언어를 따른다 (한국어가 아니면 영어)</summary>
    public static void Init(string? lang)
    {
        lang ??= CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch { "ko" => "ko", _ => "en" };
        Lang = Languages.Any(l => l.Code == lang) ? lang : "ko";
        _map = [];
        // 날짜·숫자 서식도 언어를 따른다 ("MMM d"가 영어 달 이름으로)
        var culture = CultureInfo.GetCultureInfo(Lang == "ko" ? "ko-KR" : "en-US");
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = culture;
        if (Lang == "ko") return;
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"RecipeSong.Core.Lang.{Lang}.json");
        if (s != null) _map = JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? [];
    }

    /// <summary>번역</summary>
    public static string T(string ko) => _map.TryGetValue(ko, out var s) && s.Length > 0 ? s : ko;

    /// <summary>번역 + string.Format ({0}, {1:N0} …)</summary>
    public static string F(string ko, params object?[] args)
    {
        try { return string.Format(CultureInfo.CurrentCulture, T(ko), args); }
        catch (FormatException) { return string.Format(CultureInfo.CurrentCulture, ko, args); }  // 번역의 자리표시자가 틀렸으면 원문으로
    }
}
