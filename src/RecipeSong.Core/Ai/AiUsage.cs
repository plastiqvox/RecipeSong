using System.Globalization;
using System.Text.Json;

namespace RecipeSong.Core.Ai;

/// <summary>AI 호출 한 번의 사용량과 추정 비용 (USD)</summary>
public sealed record AiUsageEntry(DateTime At, string Provider, string Model,
    long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, int WebSearches, double Cost);

/// <summary>
/// 모델별 단가 (USD, 100만 토큰당)와 웹 검색 단가 (1천 회당). 공개 단가를 옮겨 둔 추정치라 실제 청구와 조금 다를 수 있다.
/// </summary>
public static class AiPricing
{
    private sealed record Price(double Input, double Output, double CacheRead, double CacheWrite);

    private static readonly Dictionary<string, Price> Models = new()
    {
        ["claude-fable-5-1"] = new(10, 50, 1.0, 12.5),
        ["claude-opus-5-5"] = new(4, 20, 0.20, 5),
        ["claude-opus-5"] = new(5, 25, 0.50, 6.25),
        ["claude-opus-4-8"] = new(5, 25, 0.50, 6.25),
        ["claude-sonnet-5-5"] = new(2, 10, 0.20, 2.5),
        ["claude-sonnet-5"] = new(2, 10, 0.20, 2.5),
        ["claude-haiku-5-5"] = new(0.10, 0.50, 0.01, 0.125),
        ["gpt-5"] = new(1.25, 10, 0.125, 1.25),
        ["gpt-5-mini"] = new(0.25, 2, 0.025, 0.25),
    };

    /// <summary>Claude 웹 검색과 OpenAI web_search 모두 1천 회당 $10</summary>
    public const double WebSearchPer1000 = 10;

    /// <summary>모르는 모델은 같은 계열 기본값으로 (Claude → Sonnet, GPT → gpt-5)</summary>
    public static double Cost(string model, long input, long output, long cacheRead, long cacheWrite, int searches)
    {
        var p = Models.FirstOrDefault(kv => model.StartsWith(kv.Key, StringComparison.Ordinal)).Value
                ?? (model.StartsWith("claude", StringComparison.Ordinal) ? Models["claude-sonnet-5-5"] : Models["gpt-5"]);
        return (input * p.Input + output * p.Output + cacheRead * p.CacheRead + cacheWrite * p.CacheWrite) / 1_000_000
               + searches * WebSearchPer1000 / 1000;
    }
}

/// <summary>
/// AI 사용 기록: %LOCALAPPDATA%\RecipeSong\ai-usage.json. 오늘·이번 주 사용액(한도 확인)과 충전액 대비 남은 크레딧 추정에 쓴다.
/// Crate에서 쓴 것만 센다 — 같은 키를 다른 곳에서도 쓰면 실제 잔액은 더 적다.
/// </summary>
public sealed class AiUsageLedger
{
    private readonly string _file = Path.Combine(JsonCache<object>.Dir, "ai-usage.json");
    private readonly List<AiUsageEntry> _entries;

    /// <summary>최근 n번 검색 비용 (예상 비용 안내용)</summary>
    public List<double> Recent(int n) => [.. _entries.TakeLast(n).Select(e => e.Cost)];
    private readonly Lock _lock = new();

    public event Action? Changed;

    public AiUsageLedger()
    {
        try
        {
            _entries = File.Exists(_file) ? JsonSerializer.Deserialize<List<AiUsageEntry>>(File.ReadAllText(_file)) ?? [] : [];
        }
        catch (JsonException) { _entries = []; }
    }

    public AiUsageEntry Add(string provider, string model, long input, long output, long cacheRead = 0, long cacheWrite = 0, int searches = 0)
    {
        var e = new AiUsageEntry(DateTime.Now, provider, model, input, output, cacheRead, cacheWrite, searches,
            AiPricing.Cost(model, input, output, cacheRead, cacheWrite, searches));
        lock (_lock)
        {
            _entries.Add(e);
            // 1년 넘은 기록은 버린다
            _entries.RemoveAll(x => x.At < DateTime.Now.AddYears(-1));
            File.WriteAllText(_file, JsonSerializer.Serialize(_entries));
        }
        Changed?.Invoke();
        return e;
    }

    public double Spent(DateTime since, string? provider = null)
    {
        lock (_lock)
            return _entries.Where(e => e.At >= since && (provider == null || e.Provider == provider)).Sum(e => e.Cost);
    }

    public static DateTime TodayStart => DateTime.Today;

    /// <summary>이번 주 월요일 0시</summary>
    public static DateTime WeekStart => DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));

    public static string Money(double usd) => usd < 0.01 && usd > 0 ? "<$0.01" : "$" + usd.ToString("0.00", CultureInfo.InvariantCulture);
}
