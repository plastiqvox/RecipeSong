using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RecipeSong.Core.Ai;

/// <summary>
/// GPT 커넥터: OpenAI Responses API (POST /v1/responses)의 web_search 도구로 찾고,
/// 결과는 JSON 스키마 출력(strict)으로 받는다.
/// </summary>
public sealed class GptScout(AiConnectorSettings settings, AiUsageLedger? ledger = null) : IAiScout
{
    private static readonly HttpClient Http = new() { BaseAddress = new Uri("https://api.openai.com/"), Timeout = TimeSpan.FromMinutes(6) };

    public string Name => "GPT";
    public bool IsReady => settings.GptEnabled && settings.GetGptKey() != null;

    public async Task<string?> TestAsync(CancellationToken ct = default)
    {
        if (settings.GetGptKey() is not { } key) return L.T("API 키가 없어요");
        try
        {
            await PostAsync(key, new JsonObject
            {
                ["model"] = settings.GptModel,
                ["input"] = "ping",
                ["max_output_tokens"] = 16,
            }, ct);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex.Message;
        }
    }

    public async Task<string?> CheckKeyAsync(CancellationToken ct = default)
    {
        if (settings.GetGptKey() is not { } key) return L.T("키 없음");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var resp = await Http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode ? null
                : resp.StatusCode == System.Net.HttpStatusCode.Unauthorized ? L.T("키 만료·삭제됨") : L.T("키 확인 실패");
        }
        catch (HttpRequestException) { return L.T("키 확인 실패"); }
    }

    public async Task<IReadOnlyList<AiPick>> FindAsync(
        Period period, string taste, IReadOnlyCollection<string> exclude, string? focusRegion,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var key = settings.GetGptKey() ?? throw new InvalidOperationException(L.T("OpenAI API 키가 없어요"));
        progress?.Report(L.T("GPT가 웹에서 신보 찾는 중… (1~2분)"));
        var prompt = AiPrompt.Build(period, taste, exclude, focusRegion, "Answer with what you found in the required JSON format.");
        AiDebugLog.Write(Name, "보낸 지시: " + prompt.ReplaceLineEndings(" ⏎ "));
        var body = new JsonObject
        {
            ["model"] = settings.GptModel,
            ["instructions"] = AiPrompt.System,
            ["input"] = prompt,
            ["tools"] = new JsonArray { new JsonObject { ["type"] = "web_search" } },
            ["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = "releases",
                    ["strict"] = true,
                    ["schema"] = JsonSerializer.SerializeToNode(AiPrompt.Schema),
                },
            },
        };
        // 추론 모델이면 생각 깊이를 중간으로 (다른 모델은 이 필드를 받지 않는다)
        if (settings.GptModel.StartsWith("gpt-5", StringComparison.Ordinal) || settings.GptModel.StartsWith('o'))
            body["reasoning"] = new JsonObject { ["effort"] = "medium" };

        using var doc = await PostAsync(key, body, ct);
        // output: [web_search_call..., reasoning..., message{ content: [output_text{ text }] }]
        var text = doc.RootElement.GetProperty("output").EnumerateArray()
            .Where(o => o.GetProperty("type").GetString() == "message")
            .SelectMany(o => o.GetProperty("content").EnumerateArray())
            .Where(c => c.GetProperty("type").GetString() == "output_text")
            .Select(c => c.GetProperty("text").GetString())
            .LastOrDefault(t => !string.IsNullOrWhiteSpace(t))
            ?? throw new InvalidOperationException(L.T("GPT가 결과를 돌려주지 않았어요 (거절됐거나 출력 한도에 걸렸어요)"));
        using var result = JsonDocument.Parse(text);
        var picks = AiPrompt.Parse(result.RootElement.GetProperty("releases"), $"GPT {settings.GptModel}");
        AiDebugLog.Write(Name, $"제출 {picks.Count}장: " + string.Join(" | ", picks.Select(p => $"{p.Artist} – {p.Album} ({p.ReleaseDate})")));
        return picks;
    }

    private async Task<JsonDocument> PostAsync(string key, JsonObject body, CancellationToken ct)
    {
        var doc = await SendAsync(key, body, ct);
        Record(doc.RootElement);
        return doc;
    }

    // 사용량 기록: usage.input_tokens (캐시된 토큰 포함), output_tokens (추론 포함), 웹 검색 = web_search_call 항목 수
    private void Record(JsonElement root)
    {
        if (ledger == null || !root.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return;
        long input = u.TryGetProperty("input_tokens", out var i) ? i.GetInt64() : 0;
        long cached = u.TryGetProperty("input_tokens_details", out var d) && d.TryGetProperty("cached_tokens", out var c) ? c.GetInt64() : 0;
        long output = u.TryGetProperty("output_tokens", out var o) ? o.GetInt64() : 0;
        int searches = root.TryGetProperty("output", out var items)
            ? items.EnumerateArray().Count(x => x.TryGetProperty("type", out var t) && t.GetString() == "web_search_call") : 0;
        var model = root.TryGetProperty("model", out var m) ? m.GetString() ?? settings.GptModel : settings.GptModel;
        ledger.Add(Name, model, input - cached, output, cached, 0, searches);
    }

    private static async Task<JsonDocument> SendAsync(string key, JsonObject body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/responses") { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var resp = await Http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            // {"error": {"message": "..."}}
            string? message = null;
            try { message = JsonDocument.Parse(json).RootElement.GetProperty("error").GetProperty("message").GetString(); }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
            throw new HttpRequestException($"{(int)resp.StatusCode} {message ?? resp.ReasonPhrase}");
        }
        return JsonDocument.Parse(json);
    }
}
