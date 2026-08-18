using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MoaiCode.Core.Agent;
using MoaiCode.Core.Messages;
using MoaiCode.Providers;

namespace MoaiCode.Cli;

/// <summary>
/// stream-json 프로토콜(NDJSON: 이벤트 1개 = 1줄). claude CLI 의 --output-format stream-json /
/// --input-format stream-json 과 호환되게 맞춰, 그쪽을 쓰던 브리지·툴이 그대로 붙게 한다.
///
/// 출력: system/init → (assistant: text|tool_use, user: tool_result)* → result
/// 입력(--input-format stream-json): stdin 에서 {"type":"user","message":{...}} 를 한 줄씩 읽어
/// 턴을 이어간다. 프로세스가 살아 있는 동안 대화 맥락(QueryEngine)이 유지된다.
/// </summary>
public static class StreamJsonRunner
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    /// <summary>단발: 프롬프트 하나를 처리하고 끝낸다.</summary>
    public static async Task<int> RunOnceAsync(QueryEngine engine, string prompt, CancellationToken ct)
    {
        var sessionId = NewSessionId();
        EmitInit(sessionId);
        var code = await RunTurnAsync(engine, prompt, sessionId, ct).ConfigureAwait(false);
        return code;
    }

    /// <summary>영속: stdin 의 NDJSON 턴을 EOF 까지 계속 처리한다.</summary>
    public static async Task<int> RunLoopAsync(QueryEngine engine, CancellationToken ct)
    {
        var sessionId = NewSessionId();
        EmitInit(sessionId);

        var last = 0;
        while (!ct.IsCancellationRequested)
        {
            var line = await Console.In.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                break;   // EOF — 상대가 stdin 을 닫았다.
            }

            var text = ExtractUserText(line);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;   // 빈 줄·해석 불가·비-user 이벤트는 건너뛴다.
            }

            last = await RunTurnAsync(engine, text!, sessionId, ct).ConfigureAwait(false);
        }

        return last;
    }

    /// <summary>
    /// stdin 한 줄에서 사용자 텍스트를 뽑는다. content 는 블록 배열이 표준이지만
    /// 문자열로 오는 구현도 있어 둘 다 받는다.
    /// </summary>
    public static string? ExtractUserText(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }

        if (node?["type"]?.GetValue<string>() is not "user")
        {
            return null;
        }

        var content = node["message"]?["content"];
        if (content is JsonValue v && v.TryGetValue<string>(out var s))
        {
            return s;
        }

        if (content is not JsonArray arr)
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (var b in arr)
        {
            if (b?["type"]?.GetValue<string>() is "text" && b["text"] is JsonValue tv
                && tv.TryGetValue<string>(out var t))
            {
                sb.Append(t);
            }
        }

        return sb.Length > 0 ? sb.ToString() : null;
    }

    private static async Task<int> RunTurnAsync(
        QueryEngine engine, string prompt, string sessionId, CancellationToken ct)
    {
        var run = new StringBuilder();     // 아직 안 내보낸 텍스트 런
        var full = new StringBuilder();    // 이번 턴 전체 답변(result 용)
        var hadError = false;
        var stopReason = "end_turn";
        string? errMsg = null;
        var errCode = 0;

        // 추론 마커는 델타 경계에 걸쳐 쪼개져 오므로, 텍스트 런을 모아서 필터링한 뒤 내보낸다.
        void FlushText()
        {
            if (run.Length == 0)
            {
                return;
            }

            var clean = ThinkFilter.Strip(run.ToString());
            run.Clear();
            if (clean.Length == 0)
            {
                return;
            }

            if (full.Length > 0)
            {
                full.Append('\n');
            }

            full.Append(clean);
            EmitAssistant(sessionId, TextBlockNode(clean));
        }

        try
        {
            await foreach (var ev in engine.SubmitAsync(prompt, ct).WithCancellation(ct))
            {
                switch (ev)
                {
                    case TextDelta d:
                        run.Append(d.Text);
                        break;

                    case ToolCallRequested t:
                        FlushText();
                        EmitAssistant(sessionId, ToolUseNode(t.Block));
                        break;

                    case ToolExecuted x:
                        hadError |= x.IsError;
                        EmitToolResult(sessionId, x);
                        break;

                    case TurnCompleted tc:
                        FlushText();
                        stopReason = tc.StopReason;
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            FlushText(); errCode = 130; errMsg = "execution canceled."; stopReason = "canceled";
        }
        catch (ProviderException ex)
        {
            FlushText(); errCode = 2; errMsg = $"provider error: {ex.Message}"; stopReason = "error";
        }
        catch (Exception ex)
        {
            FlushText(); errCode = 1; errMsg = $"execution error: {ex.Message}"; stopReason = "error";
        }

        FlushText();

        var u = engine.CumulativeUsage;
        var isError = errCode != 0 || hadError;
        var result = new JsonObject
        {
            ["type"] = "result",
            ["subtype"] = isError ? "error" : "success",
            ["session_id"] = sessionId,
            ["is_error"] = isError,
            ["stop_reason"] = stopReason,
            ["result"] = errMsg ?? full.ToString(),
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = u.InputTokens,
                ["output_tokens"] = u.OutputTokens,
                ["cache_read_input_tokens"] = u.CacheReadTokens,
                ["cache_creation_input_tokens"] = u.CacheCreationTokens,
            },
        };
        Emit(result);
        return errCode;
    }

    private static void EmitInit(string sessionId) => Emit(new JsonObject
    {
        ["type"] = "system",
        ["subtype"] = "init",
        ["session_id"] = sessionId,
    });

    private static void EmitAssistant(string sessionId, JsonNode block) => Emit(new JsonObject
    {
        ["type"] = "assistant",
        ["session_id"] = sessionId,
        ["message"] = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = new JsonArray(block),
        },
    });

    // 툴 결과는 claude 와 같이 'user' 이벤트의 tool_result 블록으로 돌려준다.
    private static void EmitToolResult(string sessionId, ToolExecuted x) => Emit(new JsonObject
    {
        ["type"] = "user",
        ["session_id"] = sessionId,
        ["message"] = new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = x.ToolUseId,
                ["is_error"] = x.IsError,
                ["content"] = x.Output,
            }),
        },
    });

    private static JsonNode TextBlockNode(string text) => new JsonObject
    {
        ["type"] = "text",
        ["text"] = text,
    };

    private static JsonNode ToolUseNode(ToolUseBlock b) => new JsonObject
    {
        ["type"] = "tool_use",
        ["id"] = b.Id,
        ["name"] = b.Name,
        ["input"] = JsonNode.Parse(b.Input.GetRawText()) ?? new JsonObject(),
    };

    // NDJSON: 한 줄 = 이벤트 1개. 상대가 readline 으로 읽으므로 줄바꿈이 섞이면 안 된다.
    private static void Emit(JsonNode node)
    {
        Console.Out.WriteLine(node.ToJsonString(Compact));
        Console.Out.Flush();
    }

    private static string NewSessionId() =>
        "sess-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
}
