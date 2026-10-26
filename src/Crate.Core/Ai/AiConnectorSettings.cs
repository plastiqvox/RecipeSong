using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Crate.Core.Ai;

/// <summary>
/// AI 커넥터 설정 (Claude, GPT): %LOCALAPPDATA%\Crate\ai-connector.json.
/// API 키는 Windows 사용자 계정으로 암호화(DPAPI)해서 저장한다 — 다른 계정·PC에서는 풀 수 없다.
/// 키를 저장하지 않으면 ANTHROPIC_API_KEY / OPENAI_API_KEY 환경 변수를 쓴다.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AiConnectorSettings
{
    public static readonly (string Id, string Label)[] Models =
    [
        ("claude-opus-5-5", "Claude Opus 5.5 (기본, 가장 꼼꼼함)"),
        ("claude-sonnet-5-5", "Claude Sonnet 5.5 (빠르고 저렴함)"),
        ("claude-haiku-5-5", "Claude Haiku 5.5 (가장 저렴함)"),
    ];

    /// <summary>"claude-sonnet-5-5" → "Claude Sonnet 5.5" (카드 배지·사이드바 표시용)</summary>
    public static string ClaudeLabel(string modelId) =>
        Models.FirstOrDefault(m => m.Id == modelId).Label?.Split(" (")[0]
        ?? string.Join(' ', modelId.Split('-') is var p && p.Length >= 3
            ? [char.ToUpperInvariant(p[0][0]) + p[0][1..], char.ToUpperInvariant(p[1][0]) + p[1][1..], string.Join('.', p[2..])]
            : [modelId]);

    // ---- Claude (예전 설정 파일과 호환되게 이름 유지) ----
    public bool Enabled { get; set; }
    public string Model { get; set; } = "claude-opus-5-5";
    public string? EncryptedKey { get; set; }

    // ---- GPT (OpenAI) ----
    public bool GptEnabled { get; set; }
    /// <summary>OpenAI 모델 ID (Responses API + 웹 검색을 지원하는 모델)</summary>
    public string GptModel { get; set; } = "gpt-5";
    public string? GptEncryptedKey { get; set; }

    /// <summary>한 번 찾을 때 웹 검색 최대 횟수 (Claude에 적용; GPT는 모델이 정한다)</summary>
    public int MaxSearches { get; set; } = 8;

    // ---- 한도와 충전액 (커넥터별) ----
    public AiBudget ClaudeBudget { get; set; } = new();
    public AiBudget GptBudget { get; set; } = new();

    public AiBudget BudgetOf(string provider) => provider == "GPT" ? GptBudget : ClaudeBudget;

    private static string FilePath => Path.Combine(JsonCache<object>.Dir, "ai-connector.json");

    public static AiConnectorSettings Load()
    {
        AiConnectorSettings s;
        try
        {
            s = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AiConnectorSettings>(File.ReadAllText(FilePath)) ?? new()
                : new();
        }
        catch (JsonException) { return new(); }
        // 저장일을 기록하기 전에 넣은 키: 설정 파일을 처음 만든 때를 저장일로 본다
        if (s.EncryptedKey != null) s.ClaudeBudget.KeySavedAt ??= File.GetCreationTime(FilePath);
        if (s.GptEncryptedKey != null) s.GptBudget.KeySavedAt ??= File.GetCreationTime(FilePath);
        return s;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public AiConnectorSettings Clone()
    {
        var c = (AiConnectorSettings)MemberwiseClone();
        c.ClaudeBudget = ClaudeBudget with { };
        c.GptBudget = GptBudget with { };
        return c;
    }

    // ---- 키 ----
    public bool HasStoredKey => EncryptedKey != null;
    public bool HasStoredGptKey => GptEncryptedKey != null;

    public void SetKey(string? key)
    {
        EncryptedKey = Protect(key, "Crate.AiConnector");
        ClaudeBudget.KeySavedAt = EncryptedKey == null ? null : DateTime.Now;
    }

    public void SetGptKey(string? key)
    {
        GptEncryptedKey = Protect(key, "Crate.AiConnector.OpenAI");
        GptBudget.KeySavedAt = GptEncryptedKey == null ? null : DateTime.Now;
    }

    /// <summary>저장한 키, 없으면 ANTHROPIC_API_KEY</summary>
    public string? GetKey() => Unprotect(EncryptedKey, "Crate.AiConnector") ?? Env("ANTHROPIC_API_KEY");
    /// <summary>저장한 키, 없으면 OPENAI_API_KEY</summary>
    public string? GetGptKey() => Unprotect(GptEncryptedKey, "Crate.AiConnector.OpenAI") ?? Env("OPENAI_API_KEY");

    public string KeySource => EncryptedKey != null ? "저장한 키" : Env("ANTHROPIC_API_KEY") != null ? "ANTHROPIC_API_KEY 환경 변수" : "키 없음";
    public string GptKeySource => GptEncryptedKey != null ? "저장한 키" : Env("OPENAI_API_KEY") != null ? "OPENAI_API_KEY 환경 변수" : "키 없음";

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    private static string? Protect(string? key, string entropy) =>
        string.IsNullOrWhiteSpace(key) ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Encoding.UTF8.GetBytes(entropy), DataProtectionScope.CurrentUser));

    private static string? Unprotect(string? stored, string entropy)
    {
        if (stored == null) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), Encoding.UTF8.GetBytes(entropy), DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException) { return null; }
    }
}

/// <summary>
/// 커넥터 하나의 사용 한도 (USD). 비워 두면 한도 없음.
/// Credit: 콘솔에서 충전한 금액 — CreditSince 이후 Crate에서 쓴 만큼 빼서 남은 크레딧을 추정한다.
/// </summary>
public sealed record AiBudget
{
    public double? Daily { get; set; }
    public double? Weekly { get; set; }
    public double? Credit { get; set; }
    public DateTime? CreditSince { get; set; }
    /// <summary>Crate에 키를 저장한 때 (키를 새로 넣을 때마다 갱신)</summary>
    public DateTime? KeySavedAt { get; set; }
    /// <summary>콘솔에서 키를 만들 때 고른 유효기간(일). 일반 키로는 만료일을 조회할 수 없어 직접 고른다. null이면 만료 없음.</summary>
    public int? KeyDays { get; set; }

    public DateTime? KeyExpires => KeyDays is { } d && KeySavedAt is { } at ? at.Date.AddDays(d) : null;

    /// <summary>"키 만료 5일 남음" 같은 경고. 여유가 있으면 null</summary>
    public string? KeyExpiryWarning(int withinDays = 14) => KeyExpires is { } e && (e.Date - DateTime.Today).Days is var d && d <= withinDays
        ? d < 0 ? "키 만료됨" : d == 0 ? "키 오늘 만료" : $"키 만료 {d}일 남음"
        : null;

    /// <summary>한도에 닿았으면 이유, 아니면 null</summary>
    public string? Blocked(AiUsageLedger ledger, string provider)
    {
        if (Daily is { } d && ledger.Spent(AiUsageLedger.TodayStart, provider) >= d) return $"오늘 한도 {AiUsageLedger.Money(d)}에 닿았어요";
        if (Weekly is { } w && ledger.Spent(AiUsageLedger.WeekStart, provider) >= w) return $"이번 주 한도 {AiUsageLedger.Money(w)}에 닿았어요";
        if (Remaining(ledger, provider) is <= 0) return "충전한 크레딧을 다 쓴 것 같아요";
        return null;
    }

    /// <summary>선불 크레딧은 보통 충전 후 1년에 만료된다 (콘솔에서 정확한 날짜 확인). 충전액을 넣은 날을 충전일로 본다.</summary>
    public DateTime? CreditExpires => Credit != null && CreditSince is { } since ? since.Date.AddYears(1) : null;

    public double? Remaining(AiUsageLedger ledger, string provider) =>
        Credit is { } c ? c - ledger.Spent(CreditSince ?? DateTime.MinValue, provider) : null;
}
