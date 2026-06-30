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
public sealed class OpenAiChatModel : IChatModel
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _model;

    public OpenAiChatModel(HttpClient http, string baseUrl, string apiKey, string model)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _model = model;
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
            throw new ProviderException((int)resp.StatusCode, Truncate(errBody, 500));
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var toolAccum = new SortedDictionary<int, ToolCallBuilder>();
        var usage = new Usage(0, 0);
        var stopReason = "end_turn";

        await foreach (var data in SseReader.ReadDataLinesAsync(stream, ct).ConfigureAwait(false))
        {
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
                            yield return new TextDelta(text);
                        }
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

    private sealed class ToolCallBuilder
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public StringBuilder Args { get; } = new();
    }
}
