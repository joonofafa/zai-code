using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MoaiCode.Core.Agent;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;
using MoaiCode.Providers.Http;

namespace MoaiCode.Providers.OpenAi;

/// <summary>
/// OpenAI 호환 chat/completions 스트리밍 모델. OpenAI / Ollama / DeepSeek 등
/// OpenAI 호환 엔드포인트를 단일 경로로 지원 (TS openaiShim의 핵심 경로).
/// Phase 1 범위: 텍스트 + 툴콜 스트리밍. reasoning 포맷 변형/responses API는 후속.
/// </summary>
public sealed class OpenAiChatModel : IChatModel, IModelControl
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private string _model;

    public OpenAiChatModel(HttpClient http, string baseUrl, string apiKey, string model)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _model = model;
    }

    /// <summary>현재 모델 id. 교체하면 다음 요청부터 즉시 반영(/model 명령).</summary>
    public string CurrentModel
    {
        get => _model;
        set { if (!string.IsNullOrWhiteSpace(value)) _model = value; }
    }

    /// <summary>{baseUrl}/models 에서 사용 가능한 모델 id 목록 조회 (OpenAI 호환). 실패 시 빈 목록.</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/models");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return Array.Empty<string>();
            }

            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            // OpenAI 형식: { "data": [ { "id": "..." }, ... ] } / 또는 최상위 배열.
            var arr = root.ValueKind == JsonValueKind.Array ? root
                : root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array ? d
                : default;
            if (arr.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var ids = new List<string>();
            foreach (var m in arr.EnumerateArray())
            {
                if (m.ValueKind == JsonValueKind.String)
                {
                    ids.Add(m.GetString()!);
                }
                else if (m.ValueKind == JsonValueKind.Object
                         && m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    ids.Add(id.GetString()!);
                }
            }

            return ids;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Array.Empty<string>();
        }
    }

    // 디버그: MOAI_DEBUG_SSE 설정 시 원시 SSE/오류를 ~/.moai/sse-debug.log 에 남긴다(응답 포맷 진단용).
    private static readonly bool DebugSse =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MOAI_DEBUG_SSE"));

    private static readonly string DebugPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".moai", "sse-debug.log");

    private static void DebugLog(string line)
    {
        try
        {
            var dir = Path.GetDirectoryName(DebugPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var existed = File.Exists(DebugPath);
            File.AppendAllText(DebugPath, line + "\n");
            if (!existed) MoaiCode.Core.FilePermissions.RestrictFileToUser(DebugPath); // 응답 본문 포함 → 0600
        }
        catch
        {
            // 로깅 실패는 무시.
        }
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        IReadOnlyList<Message> messages,
        IReadOnlyList<ITool> tools,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["model"] = _model,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            // max_tokens 미전송 시 일부 게이트웨이(open-moai 등)가 1024 로 캡핑 → 응답이 중간에 잘려
            // 모델이 작업을 끝내기 전에 멈춘다. 넉넉히 보낸다(env MOAI_MAX_TOKENS, 기본 8192).
            ["max_tokens"] = MaxOutputTokens(),
            ["messages"] = BuildMessages(messages),
        };

        var toolsArr = BuildTools(tools);
        if (toolsArr is not null)
        {
            body["tools"] = toolsArr;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions");
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var resp = await _http
            .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (DebugSse) DebugLog($"[ERROR {(int)resp.StatusCode}] model={_model}\n{errBody}");
            throw new ProviderException((int)resp.StatusCode, Truncate(errBody, 500));
        }

        if (DebugSse) DebugLog($"\n=== {DateTime.Now:HH:mm:ss} POST chat/completions model={_model} ===");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var toolAccum = new SortedDictionary<int, ToolCallBuilder>();
        var usage = new Usage(0, 0);
        var stopReason = "end_turn";

        // 추론 모델(reasoning) 대응: 일부 게이트웨이는 답을 delta.content 가 아니라
        // delta.reasoning_content / delta.reasoning 로 흘린다. content 가 하나도 안 오고
        // 툴콜도 없으면(=빈 응답) 모아둔 reasoning 을 답변으로 폴백 방출한다.
        var reasoning = new StringBuilder();
        var emittedContent = false;

        await foreach (var data in SseReader.ReadDataLinesAsync(stream, ct).ConfigureAwait(false))
        {
            if (DebugSse) DebugLog("data: " + data);
            if (data == "[DONE]")
            {
                break;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(data);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;

                // 일부 게이트웨이는 200 OK + SSE 본문에 error 를 담아 보낸다(예: 모델 비활성/일시 오류).
                // 예전엔 choices 가 없어 그냥 skip → 빈 응답으로 삼켜졌다. 이제 예외로 노출한다.
                if (root.TryGetProperty("error", out var errEl) && errEl.ValueKind != JsonValueKind.Null)
                {
                    throw new ProviderException(ErrorStatus(errEl), "모델 응답 오류 — " + ExtractErrorMessage(errEl));
                }

                if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                {
                    usage = ParseUsage(u);
                }

                if (!root.TryGetProperty("choices", out var choices)
                    || choices.ValueKind != JsonValueKind.Array
                    || choices.GetArrayLength() == 0)
                {
                    continue;
                }

                var choice = choices[0];

                if (choice.TryGetProperty("delta", out var delta)
                    && delta.ValueKind == JsonValueKind.Object)
                {
                    if (delta.TryGetProperty("content", out var content)
                        && content.ValueKind == JsonValueKind.String)
                    {
                        var text = content.GetString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            emittedContent = true;
                            yield return new TextDelta(text);
                        }
                    }

                    // 추론 필드 누적 (reasoning_content / reasoning). content 폴백용.
                    if ((delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
                        || (delta.TryGetProperty("reasoning", out rc) && rc.ValueKind == JsonValueKind.String))
                    {
                        reasoning.Append(rc.GetString());
                    }

                    if (delta.TryGetProperty("tool_calls", out var tcs)
                        && tcs.ValueKind == JsonValueKind.Array)
                    {
                        AccumulateToolCalls(tcs, toolAccum);
                    }
                }

                if (choice.TryGetProperty("finish_reason", out var fr)
                    && fr.ValueKind == JsonValueKind.String)
                {
                    stopReason = fr.GetString() ?? stopReason;
                }
            }
        }

        // 빈 응답 폴백: content 도 툴콜도 없는데 reasoning 만 왔다면(추론 모델), 그 reasoning 을
        // 답변으로 방출한다. 안 그러면 "빈 응답 → 넛지 반복 → 답변 없음"이 된다.
        var hasTools = toolAccum.Values.Any(b => !string.IsNullOrEmpty(b.Name));
        if (!emittedContent && !hasTools && reasoning.Length > 0)
        {
            yield return new TextDelta(reasoning.ToString());
        }

        foreach (var b in toolAccum.Values)
        {
            if (string.IsNullOrEmpty(b.Name))
            {
                continue;
            }

            yield return new ToolCallRequested(
                new ToolUseBlock(b.Id, b.Name, ParseArgs(b.Args)));
        }

        yield return new TurnCompleted(usage, stopReason);
    }

    private static void AccumulateToolCalls(JsonElement tcs, SortedDictionary<int, ToolCallBuilder> acc)
    {
        foreach (var tc in tcs.EnumerateArray())
        {
            var idx = tc.TryGetProperty("index", out var ie) && ie.ValueKind == JsonValueKind.Number
                ? ie.GetInt32()
                : 0;

            if (!acc.TryGetValue(idx, out var b))
            {
                b = new ToolCallBuilder();
                acc[idx] = b;
            }

            if (tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
            {
                b.Id = idEl.GetString() ?? b.Id;
            }

            if (tc.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
            {
                if (fn.TryGetProperty("name", out var ne) && ne.ValueKind == JsonValueKind.String)
                {
                    b.Name += ne.GetString();
                }

                if (fn.TryGetProperty("arguments", out var ae) && ae.ValueKind == JsonValueKind.String)
                {
                    b.Args.Append(ae.GetString());
                }
            }
        }
    }

    private static JsonElement ParseArgs(StringBuilder args)
    {
        var json = args.Length > 0 ? args.ToString() : "{}";
        try
        {
            using var d = JsonDocument.Parse(json);
            return d.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var d = JsonDocument.Parse("{}");
            return d.RootElement.Clone();
        }
    }

    private static Usage ParseUsage(JsonElement u)
    {
        var input = u.TryGetProperty("prompt_tokens", out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetInt32() : 0;
        var output = u.TryGetProperty("completion_tokens", out var c) && c.ValueKind == JsonValueKind.Number
            ? c.GetInt32() : 0;

        var cacheRead = 0;
        if (u.TryGetProperty("prompt_tokens_details", out var ptd)
            && ptd.ValueKind == JsonValueKind.Object
            && ptd.TryGetProperty("cached_tokens", out var ctk)
            && ctk.ValueKind == JsonValueKind.Number)
        {
            cacheRead = ctk.GetInt32();
        }

        return new Usage(input, output, cacheRead);
    }

    private static JsonArray BuildMessages(IReadOnlyList<Message> messages)
    {
        var arr = new JsonArray();
        foreach (var m in messages)
        {
            switch (m)
            {
                case SystemMessage s:
                    arr.Add(new JsonObject { ["role"] = "system", ["content"] = s.Text });
                    break;

                case UserMessage u:
                    arr.Add(new JsonObject { ["role"] = "user", ["content"] = u.Text });
                    break;

                case AssistantMessage a:
                    arr.Add(BuildAssistant(a));
                    break;

                case ToolResultMessage tr:
                    arr.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = tr.ToolUseId,
                        ["content"] = tr.Output,
                    });
                    break;
            }
        }

        return arr;
    }

    private static JsonObject BuildAssistant(AssistantMessage a)
    {
        var text = string.Concat(a.Content.OfType<TextBlock>().Select(t => t.Text));
        var toolUses = a.Content.OfType<ToolUseBlock>().ToList();

        var obj = new JsonObject { ["role"] = "assistant" };
        obj["content"] = text.Length > 0 ? text : null;

        if (toolUses.Count > 0)
        {
            var tc = new JsonArray();
            foreach (var tu in toolUses)
            {
                tc.Add(new JsonObject
                {
                    ["id"] = tu.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tu.Name,
                        ["arguments"] = tu.Input.GetRawText(),
                    },
                });
            }

            obj["tool_calls"] = tc;
        }

        return obj;
    }

    private static JsonArray? BuildTools(IReadOnlyList<ITool> tools)
    {
        if (tools.Count == 0)
        {
            return null;
        }

        var arr = new JsonArray();
        foreach (var t in tools)
        {
            arr.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = JsonNode.Parse(t.InputSchema.GetRawText()),
                },
            });
        }

        return arr;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    // SSE 본문 error 객체에서 상태코드 추출(code/status 가 숫자면 사용, 아니면 400=즉시 노출).
    private static int ErrorStatus(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.Object)
        {
            if (error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number
                && c.TryGetInt32(out var ci))
            {
                return ci;
            }

            if (error.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Number
                && s.TryGetInt32(out var si))
            {
                return si;
            }
        }

        return 400;
    }

    // SSE 본문 error 객체에서 사람이 읽을 메시지 추출.
    private static string ExtractErrorMessage(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.String)
        {
            return error.GetString() ?? "unknown error";
        }

        if (error.ValueKind == JsonValueKind.Object)
        {
            if (error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(m.GetString()))
            {
                return m.GetString()!;
            }

            var parts = new List<string>();
            if (error.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
            {
                parts.Add(t.GetString()!);
            }

            if (error.TryGetProperty("code", out var c))
            {
                parts.Add("code=" + c.ToString());
            }

            return parts.Count > 0 ? string.Join(" ", parts) : Truncate(error.GetRawText(), 300);
        }

        return Truncate(error.ToString(), 300);
    }

    // 출력 토큰 상한. env MOAI_MAX_TOKENS 로 조정(기본 8192). 게이트웨이의 낮은 기본 캡(예:1024)으로
    // 응답이 잘려 작업 도중 멈추는 것을 방지. 합리적 범위로 클램프.
    private static int MaxOutputTokens()
    {
        var env = Environment.GetEnvironmentVariable("MOAI_MAX_TOKENS");
        if (int.TryParse(env, out var n) && n > 0)
        {
            return Math.Clamp(n, 256, 200_000);
        }

        return 8192;
    }

    private sealed class ToolCallBuilder
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public StringBuilder Args { get; } = new();
    }
}
