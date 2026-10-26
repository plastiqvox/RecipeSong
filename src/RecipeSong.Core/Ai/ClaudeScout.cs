using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;

namespace RecipeSong.Core.Ai;

/// <summary>Claude 커넥터: 웹 검색 도구로 찾고, 결과는 strict 도구(submit_releases)로 받는다.</summary>
public sealed class ClaudeScout(AiConnectorSettings settings, AiUsageLedger? ledger = null) : IAiScout
{
    private const string SubmitTool = "submit_releases";

    public string Name => "Claude";
    public bool IsReady => settings.Enabled && settings.GetKey() != null;

    public async Task<string?> TestAsync(CancellationToken ct = default)
    {
        if (settings.GetKey() is not { } key) return "API 키가 없어요";
        try
        {
            var client = new AnthropicClient { ApiKey = key };
            var resp = await client.Beta.Messages.Create(new MessageCreateParams
            {
                Model = settings.Model,
                MaxTokens = 16,
                OutputConfig = new BetaOutputConfig { Effort = Effort.Low },
                Messages = [new BetaUserMessageParam { Content = "ping" }],
            }, ct);
            Record(resp);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex.Message;
        }
    }

    public async Task<string?> CheckKeyAsync(CancellationToken ct = default)
    {
        if (settings.GetKey() is not { } key) return "키 없음";
        try
        {
            await new AnthropicClient { ApiKey = key }.Models.List(cancellationToken: ct);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex.Message.Contains("401") || ex.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase)
                ? "키 만료·삭제됨" : "키 확인 실패";
        }
    }

    public async Task<IReadOnlyList<AiPick>> FindAsync(
        Period period, string taste, IReadOnlyCollection<string> exclude, string? focusRegion,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var key = settings.GetKey() ?? throw new InvalidOperationException("Claude API 키가 없어요");
        var client = new AnthropicClient { ApiKey = key };
        var prompt = AiPrompt.Build(period, taste, exclude, focusRegion, $"찾은 결과는 {SubmitTool} 도구로 한 번에 제출하세요.");

        var tools = new List<BetaToolUnion>
        {
            new(new BetaWebSearchTool20260209 { MaxUses = settings.MaxSearches }),
            new(new BetaTool
            {
                Name = SubmitTool,
                Description = "찾은 신보 목록을 제출한다. 모든 검색이 끝난 뒤 한 번만 호출한다.",
                Strict = true,
                // strict 도구는 모든 object에 additionalProperties: false가 필요해서 스키마를 통째로 JSON으로 만든다
                InputSchema = InputSchema.FromRawUnchecked(ToRaw(AiPrompt.Schema)),
            }),
        };

        var messages = new List<BetaMessageParam> { new BetaUserMessageParam { Content = prompt } };
        bool nudged = false;
        for (int turn = 0; turn < 4; turn++)
        {
            progress?.Report(turn == 0 ? "Claude가 웹에서 신보 찾는 중… (1~2분)"
                : nudged ? "Claude에게 결과 제출을 다시 요청하는 중…" : "Claude가 검색 이어서 하는 중…");
            var resp = await client.Beta.Messages.Create(new MessageCreateParams
            {
                Model = settings.Model,
                MaxTokens = 16000,
                // 정책상 거절되면 서버가 기본 대체 모델로 다시 시도한다
                Betas = ["server-side-fallback-2026-07-01"],
                Fallbacks = new Default(),
                OutputConfig = new BetaOutputConfig { Effort = Effort.Medium },
                System = AiPrompt.System,
                Tools = tools,
                Messages = messages,
            }, ct);
            Record(resp);

            foreach (var block in resp.Content)
                if (block.TryPickToolUse(out BetaToolUseBlock? tu) && tu.Name == SubmitTool)
                {
                    var picks = AiPrompt.Parse(tu.Input["releases"], AiConnectorSettings.ClaudeLabel(resp.Model.ToString().Trim('"')));
                    AiDebugLog.Write(Name, $"제출 {picks.Count}장: " + string.Join(" | ", picks.Select(p => $"{p.Artist} – {p.Album} ({p.ReleaseDate})")));
                    return picks;
                }

            var text = string.Concat(resp.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
            AiDebugLog.Write(Name, $"turn {turn} stop={resp.StopReason} text={text}");
            if (resp.StopReason == "refusal") break;

            // 받은 내용을 그대로 붙여 이어서 요청한다
            var echo = JsonSerializer.Deserialize<List<BetaContentBlockParam>>(JsonSerializer.Serialize(resp.Content))!;
            messages.Add(new BetaAssistantMessageParam { Content = echo });
            if (resp.StopReason == "pause_turn") continue;  // 검색이 길어져 서버가 턴을 잠시 멈췄다
            if (nudged) break;
            // 검색은 끝났는데 도구로 제출하지 않고 글로 끝냈다 → 더 검색하지 말고 제출만 하라고 한 번 더 요청
            nudged = true;
            messages.Add(new BetaUserMessageParam
            {
                Content = $"더 검색하지 말고, 지금까지 찾은 발매작을 {SubmitTool} 도구로 제출해 주세요. 발매일을 확인한 것만 넣어 주세요.",
            });
        }
        throw new InvalidOperationException("Claude가 결과를 제출하지 않았어요. 잠시 뒤 다시 시도해 주세요.");
    }

    // 사용량 기록 (대체 모델로 처리됐으면 그 모델 단가로)
    private void Record(BetaMessage resp)
    {
        var u = resp.Usage;
        ledger?.Add(Name, resp.Model.ToString().Trim('"') is { Length: > 0 } m ? m : settings.Model,
            u.InputTokens, u.OutputTokens, u.CacheReadInputTokens ?? 0, u.CacheCreationInputTokens ?? 0,
            (int)(u.ServerToolUse?.WebSearchRequests ?? 0));
    }

    private static Dictionary<string, JsonElement> ToRaw(object schema) =>
        JsonSerializer.SerializeToElement(schema).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
}
