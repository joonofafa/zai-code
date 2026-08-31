using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using MoaiCode.Core.Agent;
using MoaiCode.Core.Messages;
using MoaiCode.Providers;

namespace MoaiCode.Cli;

/// <summary>
/// stream-json 프로토콜(NDJSON: 이벤트 1개 = 1줄). claude CLI 의 --output-format stream-json /
/// --input-format stream-json 과 호환되게 맞춰, 그쪽을 쓰던 브리지·툴이 그대로 붙게 한다.
///
/// 출력: system/init → (assistant: text|tool_use, user: tool_result, permission_request|result)* → result
/// 입력(--input-format stream-json): stdin 에서 {"type":"user","message":{...}} 턴과
/// {"type":"permission_response",...} 승인 응답을 한 줄씩 읽어 라우팅한다.
/// 프로세스가 살아 있는 동안 대화 맥락(QueryEngine)이 유지된다.
/// </summary>
public static class StreamJsonRunner
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    /// <summary>단발: 프롬프트 하나를 처리하고 끝난다.</summary>
    public static async Task<int> RunOnceAsync(QueryEngine engine, string prompt, CancellationToken ct)
    {
        var sessionId = NewSessionId();
        EmitInit(sessionId);
        var code = await RunTurnAsync(engine, prompt, sessionId, ct).ConfigureAwait(false);
        return code;
    }

    /// <summary>영속: stdin 의 NDJSON 을 EOF 까지 계속 처리한다. user 턴은 순차 실행,
    /// permission_response 는 게이트로 즉시 라우팅한다(턴 실행 중에도 읽어야 하므로 백그라운드 리더).</summary>
    public static async Task<int> RunLoopAsync(QueryEngine engine, CancellationToken ct)
    {
        return await RunLoopAsync(engine, null, ct).ConfigureAwait(false);
    }

    /// <summary>permission 게이트가 연결된 변형. AppBootstrap 이 만든 게이트를 주입받는다.</summary>
    public static async Task<int> RunLoopAsync(QueryEngine engine, StreamJsonPermissionGate? gate, CancellationToken ct)
    {
        var sessionId = NewSessionId();
        EmitInit(sessionId);

        // stdin 라우팅: user 턴은 채널로, permission_response 는 게이트로. 한 줄씩 읽는 건 이 태스크 하나뿐.
        var turns = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });
        var reader = Task.Run(() => ReadStdinAsync(turns, gate, ct), ct);

        var last = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // 채널이 정상적으로 닫히면 false가 반환된다. ReadAsync만 사용하면
                    // EOF에서 ChannelClosedException이 루프 밖으로 새어 나갈 수 있다.
                    while (await turns.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                    {
                        while (turns.Reader.TryRead(out var text))
                        {
                            if (string.IsNullOrWhiteSpace(text))
                            {
                                continue;   // 빈 줄·해석 불가·비-user 이벤트는 건너뛴다.
                            }

                            last = await RunTurnAsync(engine, text, sessionId, ct).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                break; // stdin EOF 또는 reader 완료
            }
        }
        finally
        {
            // 종료 시 대기 중인 승인 요청도 거부로 마감(턴이 승인 대기에 걸려 있지 않게).
            gate?.FailAllPending();
            turns.Writer.TryComplete();
            try
            {
                await reader.ConfigureAwait(false);
            }
            catch
            {
                // 리더 종료 오류는 무시.
            }
        }

        return last;
    }

    // stdin 을 계속 읽어 한 줄씩 분류한다. user → 채널, permission_response → 게이트, 그 외 → 버림.
    private static async Task ReadStdinAsync(Channel<string> turns, StreamJsonPermissionGate? gate, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await Console.In.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                break;   // stdin 닫힘(브리지 종료)
            }

            if (line is null)
            {
                break;   // EOF — 상대가 stdin 을 닫았다. 대기 중 승인은 거부 처리된다.
            }

            if (TryRoutePermissionResponse(line, gate))
            {
                continue;
            }

            var text = ExtractUserText(line);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            await turns.Writer.WriteAsync(text, ct).ConfigureAwait(false);
        }

        turns.Writer.TryComplete();
        gate?.FailAllPending();
    }

    // permission_response 이벤트면 게이트로 돌리고 true. 형식이 아니면 false(라인은 그대로 남는다).
    internal static bool TryRoutePermissionResponse(string line, StreamJsonPermissionGate? gate)
    {
        if (gate is null || string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            return false;
        }

        if (node is not JsonObject obj || StringProperty(obj, "type") is not "permission_response")
        {
            return false;
        }

        var id = StringProperty(obj, "request_id");
        var decision = StringProperty(obj, "decision");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(decision))
        {
            return true;   // permission_response 지만 필드가 못 썼다 — 라우팅은 한 것으로 간주(버림).
        }

        gate.HandleResponse(id, decision);
        return true;
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

        if (node is not JsonObject obj || StringProperty(obj, "type") is not "user")
        {
            return null;
        }

        var content = (obj["message"] as JsonObject)?["content"];
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
            if (b is JsonObject block && StringProperty(block, "type") is "text"
                && StringProperty(block, "text") is { } t)
            {
                sb.Append(t);
            }
        }

        return sb.Length > 0 ? sb.ToString() : null;
    }

    private static string? StringProperty(JsonObject obj, string name)
    {
        if (!obj.TryGetPropertyValue(name, out var value) || value is not JsonValue jsonValue)
        {
            return null;
        }

        return jsonValue.TryGetValue<string>(out var text) ? text : null;
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
    // permission 게이트도 이 경로로 출력한다(프로토콜 출력구가 하나여야 줄이 섞이지 않는다).
    internal static void Emit(JsonNode node)
    {
        Console.Out.WriteLine(node.ToJsonString(Compact));
        Console.Out.Flush();
    }

    private static string NewSessionId() =>
        "sess-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
}
