using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MoaiCode.Core;
using MoaiCode.Core.Agent;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;
using MoaiCode.Providers.Http;

namespace MoaiCode.Providers.Anthropic;

/// <summary>
/// Claude Messages API(POST /v1/messages) 스트리밍 모델. 다른 프로바이더와 같은 SSE 리더·헤더 대기 상한·재시도 계층을 쓴다.
/// 추론(thinking) 블록은 서명째 받아 <see cref="ReasoningBlock"/> 으로 넘기고, 다음 요청에 그대로 되돌려 보낸다.
/// 엔진은 기록을 고쳐 쓰는 일이 있으므로(컴팩션·응답 정리) 어긋난 블록은 API 가 버리게 한다(prefix_mismatch_behavior=drop_block).
/// </summary>
public sealed class AnthropicChatModel : IChatModel, IModelControl
{
    public const string ProviderId = "anthropic";
    internal const string ApiVersion = "2026-10-21";
    private const string BindingBeta = "thinking-binding-controls-2026-08-01";

    // 스트리밍이라 HTTP 시간 초과 걱정이 없다 — 넉넉히 준다(잘리면 이어쓰기로 버텨야 해 비싸다).
    private const int DefaultMaxTokens = 64_000;

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private string _model;

    // 서버가 거부한 선택 설정 — 같은 모델에는 다음 요청부터 처음부터 뺀다(/model 로 바뀌면 비운다).
    private readonly HashSet<string> _rejected = new(StringComparer.Ordinal);

    public AnthropicChatModel(HttpClient http, string baseUrl, string apiKey, string model)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _model = model;
    }

    public string CurrentModel
    {
        get => _model;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                _model = value;
                lock (_rejected)
                {
                    _rejected.Clear();
                }
            }
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
        => (await ModelDirectory.ListAsync(_http, ProviderKind.Anthropic, _baseUrl, _apiKey, ct).ConfigureAwait(false)).Models;

    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        IReadOnlyList<Message> messages,
        IReadOnlyList<ITool> tools,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var names = new ToolNames();
        var stripReasoning = false;
        HttpResponseMessage resp;
        while (true)
        {
            var body = BuildBody(messages, tools, names, stripReasoning);
            resp = await StreamingHttp.SendAsync(_http, BuildRequest(body), ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                break;
            }

            var errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var status = (int)resp.StatusCode;
            resp.Dispose();

            if (status == 400)
            {
                // 기록이 바뀌어 추론 블록 서명이 맞지 않음(drop_block 을 못 쓰는 경로) → 추론 블록을 빼고 한 번 더.
                if (!stripReasoning && IsSignatureMismatch(errBody))
                {
                    stripReasoning = true;
                    continue;
                }

                // 모델이 모르는 선택 설정(구형 모델의 adaptive thinking·effort 등) → 빼고 다시.
                if (RejectedSetting(errBody) is { } setting && Reject(setting))
                {
                    continue;
                }
            }

            throw new ProviderException(status, ErrorMessage(errBody));
        }

        using var _resp = resp;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var blocks = new Dictionary<int, BlockBuilder>();
        var toolUses = new List<ToolUseBlock>();
        int input = 0, output = 0, cacheRead = 0, cacheWrite = 0;
        string? stopReason = null;
        var emittedText = false;
        var sawEnd = false;

        await foreach (var data in SseReader.ReadDataLinesAsync(stream, ct).ConfigureAwait(false))
        {
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
                switch (Str(root, "type"))
                {
                    case "message_start":
                        if (root.TryGetProperty("message", out var msg) && msg.TryGetProperty("usage", out var u0))
                        {
                            ReadUsage(u0, ref input, ref output, ref cacheRead, ref cacheWrite);
                        }

                        break;

                    case "content_block_start":
                        if (root.TryGetProperty("content_block", out var cb))
                        {
                            blocks[Int(root, "index")] = BlockBuilder.Start(cb);
                        }

                        break;

                    case "content_block_delta":
                        if (blocks.TryGetValue(Int(root, "index"), out var b) && root.TryGetProperty("delta", out var delta))
                        {
                            var text = b.Apply(delta);
                            if (!string.IsNullOrEmpty(text))
                            {
                                emittedText = true;
                                yield return new TextDelta(text);
                            }
                        }

                        break;

                    case "content_block_stop":
                        if (blocks.Remove(Int(root, "index"), out var done))
                        {
                            if (done.ToReasoning() is { } reasoning)
                            {
                                yield return new ReasoningCaptured(new ReasoningBlock(ProviderId, reasoning));
                            }
                            else if (done.Type == "tool_use")
                            {
                                toolUses.Add(new ToolUseBlock(done.Id, names.Original(done.Name), ToolArguments.Parse(done.Json.ToString())));
                            }
                        }

                        break;

                    case "message_delta":
                        if (root.TryGetProperty("delta", out var md) && Str(md, "stop_reason") is { } sr)
                        {
                            stopReason = sr;
                        }

                        if (root.TryGetProperty("usage", out var u1))
                        {
                            ReadUsage(u1, ref input, ref output, ref cacheRead, ref cacheWrite);
                        }

                        break;

                    case "message_stop":
                        sawEnd = true;
                        break;

                    case "error":
                        // 스트림 도중 오류(과부하 등). 종류를 HTTP 상태로 옮겨 재시도 분류에 태운다.
                        var err = root.TryGetProperty("error", out var e) ? e : root;
                        throw new ProviderException(StatusFor(Str(err, "type")), Str(err, "message") ?? data);
                }
            }

            if (sawEnd)
            {
                break;
            }
        }

        if (!sawEnd)
        {
            throw new ProviderException(
                L10n.Get("providers.streamEndedEarly", "no message_stop"), ErrorCategory.NetworkTransient);
        }

        // 안전 분류기가 거절하면 본문 없이 끝난다 — 빈 응답 대신 이유를 보여 준다.
        if (stopReason == "refusal" && !emittedText && toolUses.Count == 0)
        {
            yield return new TextDelta(L10n.Get("providers.anthropic.refused"));
        }

        foreach (var tu in toolUses)
        {
            yield return new ToolCallRequested(tu);
        }

        // 엔진은 OpenAI 식 종료 사유를 본다(tool_calls·length).
        var mapped = stopReason switch
        {
            "tool_use" => "tool_calls",
            "max_tokens" => "max_tokens",
            "model_context_window_exceeded" => "length",
            _ => "end_turn",
        };
        yield return new TurnCompleted(new Usage(input + cacheRead + cacheWrite, output, cacheRead, cacheWrite), mapped);
    }

    // ---- 요청 ----

    private HttpRequestMessage BuildRequest(JsonObject body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/messages");
        req.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        if (!IsRejected("block_binding"))
        {
            req.Headers.TryAddWithoutValidation("anthropic-beta", BindingBeta);
        }

        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return req;
    }

    internal JsonObject BuildBody(IReadOnlyList<Message> messages, IReadOnlyList<ITool> tools, ToolNames names, bool stripReasoning)
    {
        var system = string.Join("\n\n", messages.OfType<SystemMessage>().Select(m => m.Text).Where(t => t.Length > 0));
        var body = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = MaxTokens(),
            ["stream"] = true,
        };
        if (system.Length > 0)
        {
            body["system"] = system;
        }

        body["messages"] = BuildMessages(messages, names, stripReasoning);

        if (tools.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var t in tools)
            {
                var def = new JsonObject
                {
                    ["name"] = names.Safe(t.Name),
                    ["description"] = t.Description,
                    ["input_schema"] = JsonNode.Parse(t.InputSchema.GetRawText()),
                };
                if (!IsRejected("eager_input_streaming"))
                {
                    // 큰 입력(파일 내용 등)을 버퍼링 없이 흘려받는다. 대신 검증되지 않은 JSON 이 올 수 있어
                    // 해석 실패는 ToolArguments 표식으로 엔진에 넘긴다(엔진이 실행하지 않고 오류로 돌려준다).
                    def["eager_input_streaming"] = true;
                }

                arr.Add(def);
            }

            body["tools"] = arr;
        }

        if (!IsRejected("thinking"))
        {
            var thinking = new JsonObject { ["type"] = "adaptive" };
            if (!IsRejected("block_binding"))
            {
                // 컴팩션 등으로 기록이 바뀌면 그 뒤 추론 블록은 서명이 안 맞는다 — 오류 대신 버리게 한다.
                thinking["block_binding"] = new JsonObject { ["prefix_mismatch_behavior"] = "drop_block" };
            }

            body["thinking"] = thinking;
        }

        if (!IsRejected("output_config") && ResolveEffort() is { } effort)
        {
            body["output_config"] = new JsonObject { ["effort"] = effort };
        }

        if (!IsRejected("cache_control"))
        {
            body["cache_control"] = new JsonObject { ["type"] = "ephemeral" };   // 마지막 블록까지 자동 캐시
        }

        return body;
    }

    // 엔진 기록 → Messages API 형식. 연속된 같은 역할은 한 메시지로 합친다(툴 결과 뒤 리마인더 등).
    private static JsonArray BuildMessages(IReadOnlyList<Message> messages, ToolNames names, bool stripReasoning)
    {
        var turns = new List<(string Role, JsonArray Content)>();

        void Add(string role, JsonNode block)
        {
            if (turns.Count == 0 || turns[^1].Role != role)
            {
                turns.Add((role, new JsonArray()));
            }

            turns[^1].Content.Add(block);
        }

        foreach (var m in messages)
        {
            switch (m)
            {
                case UserMessage u when u.Text.Length > 0:
                    Add("user", Text(u.Text));
                    break;

                case ToolResultMessage tr:
                    var result = new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = SafeId(tr.ToolUseId),
                        ["content"] = tr.Output.Length > 0 ? tr.Output : "(no output)",
                    };
                    if (tr.IsError)
                    {
                        result["is_error"] = true;
                    }

                    Add("user", result);
                    break;

                case AssistantMessage a:
                    var content = new List<JsonNode>();
                    if (!stripReasoning)
                    {
                        foreach (var r in a.Content.OfType<ReasoningBlock>().Where(r => r.Provider == ProviderId))
                        {
                            content.Add(JsonNode.Parse(r.Raw.GetRawText())!);
                        }
                    }

                    var text = string.Concat(a.Content.OfType<TextBlock>().Select(t => t.Text));
                    var tus = a.Content.OfType<ToolUseBlock>().ToList();
                    if (text.Length == 0 && tus.Count == 0)
                    {
                        break;   // 추론만 남은 메시지는 보내지 않는다
                    }

                    if (text.Length > 0)
                    {
                        content.Add(Text(text));
                    }

                    foreach (var tu in tus)
                    {
                        content.Add(new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = SafeId(tu.Id),
                            ["name"] = names.Safe(tu.Name),
                            ["input"] = tu.Input.ValueKind == JsonValueKind.Object
                                ? JsonNode.Parse(tu.Input.GetRawText())
                                : new JsonObject(),
                        });
                    }

                    foreach (var c in content)
                    {
                        Add("assistant", c);
                    }

                    break;
            }
        }

        // 첫 메시지는 user 여야 하고, 마지막이 assistant 면 프리필로 거부된다 — 이어 가라는 한 줄로 메운다.
        if (turns.Count > 0 && turns[0].Role != "user")
        {
            turns.Insert(0, ("user", new JsonArray { Text("(conversation continues)") }));
        }

        if (turns.Count == 0 || turns[^1].Role != "user")
        {
            turns.Add(("user", new JsonArray { Text("Continue.") }));
        }

        var arr = new JsonArray();
        foreach (var (role, content) in turns)
        {
            arr.Add(new JsonObject { ["role"] = role, ["content"] = content });
        }

        return arr;
    }

    private static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };

    // tool_use id 는 [A-Za-z0-9_-] 만 받는다 — 다른 프로바이더(z.ai 등)가 만든 기록을 이어받을 때를 위해 같은 규칙으로 바꾼다.
    internal static string SafeId(string id)
    {
        if (id.Length == 0)
        {
            return "toolu_none";
        }

        var sb = new StringBuilder(id.Length);
        foreach (var ch in id)
        {
            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_');
        }

        return sb.ToString();
    }

    private static int MaxTokens()
        => int.TryParse(Environment.GetEnvironmentVariable("MOAI_MAX_TOKENS"), out var n) && n > 0
            ? Math.Clamp(n, 256, 128_000)
            : DefaultMaxTokens;

    private static string? ResolveEffort()
    {
        var effort = (Environment.GetEnvironmentVariable("MOAI_REASONING_EFFORT")
                      ?? Environment.GetEnvironmentVariable("ZAI_REASONING_EFFORT")
                      ?? Environment.GetEnvironmentVariable("MOAI_EFFORT"))
            ?.Trim().ToLowerInvariant();
        return effort is "low" or "medium" or "high" ? effort : null;
    }

    // ---- 거부된 설정 자가 치유 ----

    private bool IsRejected(string setting)
    {
        lock (_rejected)
        {
            return _rejected.Contains(setting);
        }
    }

    private bool Reject(string setting)
    {
        lock (_rejected)
        {
            return _rejected.Add(setting);   // 이미 뺐는데 또 거부되면 false → 그대로 오류
        }
    }

    // 400 본문이 지목한 선택 설정. 구형 모델(adaptive 미지원)·프록시(베타 헤더 거부) 등.
    internal static string? RejectedSetting(string errorBody)
    {
        var m = errorBody.ToLowerInvariant();
        if (m.Contains("block_binding") || m.Contains("anthropic-beta") || m.Contains(BindingBeta))
        {
            return "block_binding";
        }

        if (m.Contains("eager_input_streaming"))
        {
            return "eager_input_streaming";
        }

        if (m.Contains("adaptive") || m.Contains("thinking.type") || m.Contains("thinking:"))
        {
            return "thinking";
        }

        if (m.Contains("output_config") || m.Contains("effort"))
        {
            return "output_config";
        }

        return m.Contains("cache_control") ? "cache_control" : null;
    }

    private static bool IsSignatureMismatch(string errorBody)
        => errorBody.Contains("bound to a different conversation", StringComparison.OrdinalIgnoreCase)
           || errorBody.Contains("Invalid `signature`", StringComparison.OrdinalIgnoreCase);

    // ---- 응답 해석 ----

    private static void ReadUsage(JsonElement u, ref int input, ref int output, ref int cacheRead, ref int cacheWrite)
    {
        if (TryInt(u, "input_tokens") is { } i) input = i;
        if (TryInt(u, "output_tokens") is { } o) output = o;
        if (TryInt(u, "cache_read_input_tokens") is { } r) cacheRead = r;
        if (TryInt(u, "cache_creation_input_tokens") is { } w) cacheWrite = w;
    }

    // 오류 종류 → HTTP 상태(재시도 분류용). overloaded_error 는 529(과부하) 로 재시도된다.
    private static int StatusFor(string? errorType) => errorType switch
    {
        "overloaded_error" => 529,
        "rate_limit_error" => 429,
        "api_error" => 500,
        "authentication_error" => 401,
        "permission_error" => 403,
        "not_found_error" => 404,
        "request_too_large" => 413,
        _ => 400,
    };

    private static string ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && Str(e, "message") is { } msg)
            {
                return msg.Length <= 500 ? msg : msg[..500] + "…";
            }
        }
        catch (JsonException)
        {
            // 본문이 JSON 이 아니면 그대로
        }

        return body.Length <= 500 ? body : body[..500] + "…";
    }

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int Int(JsonElement e, string name) => TryInt(e, name) ?? 0;

    private static int? TryInt(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : null;

    /// <summary>스트림의 콘텐츠 블록 하나를 모은다.</summary>
    private sealed class BlockBuilder
    {
        public string Type { get; private init; } = "";
        public string Id { get; private init; } = "";
        public string Name { get; private init; } = "";
        public StringBuilder Json { get; } = new();
        private readonly StringBuilder _thinking = new();
        private string _signature = "";
        private string? _redactedData;

        public static BlockBuilder Start(JsonElement block)
        {
            var b = new BlockBuilder
            {
                Type = Str(block, "type") ?? "",
                Id = Str(block, "id") ?? "",
                Name = Str(block, "name") ?? "",
            };
            b._thinking.Append(Str(block, "thinking"));
            b._signature = Str(block, "signature") ?? "";
            b._redactedData = Str(block, "data");
            return b;
        }

        /// <summary>델타를 반영한다. 화면에 보낼 본문 텍스트가 있으면 돌려준다.</summary>
        public string? Apply(JsonElement delta)
        {
            switch (Str(delta, "type"))
            {
                case "text_delta":
                    return Str(delta, "text");
                case "thinking_delta":
                    _thinking.Append(Str(delta, "thinking"));
                    break;
                case "signature_delta":
                    _signature += Str(delta, "signature");
                    break;
                case "input_json_delta":
                    Json.Append(Str(delta, "partial_json"));
                    break;
            }

            return null;
        }

        /// <summary>추론 블록이면 되돌려 보낼 원문(JSON 객체), 아니면 null.</summary>
        public JsonElement? ToReasoning()
        {
            JsonObject? raw = Type switch
            {
                "thinking" => new JsonObject { ["type"] = "thinking", ["thinking"] = _thinking.ToString(), ["signature"] = _signature },
                "redacted_thinking" => new JsonObject { ["type"] = "redacted_thinking", ["data"] = _redactedData ?? "" },
                _ => null,
            };
            if (raw is null)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(raw.ToJsonString());
            return doc.RootElement.Clone();
        }
    }
}

/// <summary>
/// 툴 이름 대응표. Claude 는 [A-Za-z0-9_-]{1,128} 만 받으므로 다른 글자(MCP 툴의 '.' 등)는 바꿔 보내고,
/// 응답의 tool_use 이름은 원래 이름으로 되돌린다.
/// </summary>
internal sealed class ToolNames
{
    private readonly Dictionary<string, string> _toOriginal = new(StringComparer.Ordinal);

    public string Safe(string name)
    {
        var sb = new StringBuilder(Math.Min(name.Length, 128));
        foreach (var ch in name)
        {
            if (sb.Length == 128)
            {
                break;
            }

            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_');
        }

        var safe = sb.Length == 0 ? "tool" : sb.ToString();
        _toOriginal.TryAdd(safe, name);
        return safe;
    }

    public string Original(string safe) => _toOriginal.TryGetValue(safe, out var name) ? name : safe;
}
