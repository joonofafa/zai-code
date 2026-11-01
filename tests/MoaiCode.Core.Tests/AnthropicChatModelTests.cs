using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MoaiCode.Core.Agent;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;
using MoaiCode.Providers;
using MoaiCode.Providers.Anthropic;
using MoaiCode.Providers.OpenAi;
using Xunit;

namespace MoaiCode.Core.Tests;

/// <summary>Claude Messages API 스트리밍·요청 형식. 실제 키 없이 기록된 SSE 로 검증한다.</summary>
[Collection("EnvMutating")]
public sealed class AnthropicChatModelTests : IDisposable
{
    private readonly string? _effort = Environment.GetEnvironmentVariable("MOAI_REASONING_EFFORT");

    public AnthropicChatModelTests() => Environment.SetEnvironmentVariable("MOAI_REASONING_EFFORT", null);

    public void Dispose() => Environment.SetEnvironmentVariable("MOAI_REASONING_EFFORT", _effort);

    private static string Sse(params string[] events)
        => string.Concat(events.Select(e =>
        {
            var type = JsonDocument.Parse(e).RootElement.GetProperty("type").GetString();
            return $"event: {type}\ndata: {e}\n\n";
        }));

    private static readonly string ToolTurn = Sse(
        """{"type":"message_start","message":{"id":"msg_1","usage":{"input_tokens":10,"cache_read_input_tokens":90,"cache_creation_input_tokens":5,"output_tokens":1}}}""",
        """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}""",
        """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"plan"}}""",
        """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"SIG=="}}""",
        """{"type":"content_block_stop","index":0}""",
        """{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}""",
        """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Reading."}}""",
        """{"type":"content_block_stop","index":1}""",
        """{"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"toolu_1","name":"mcp_files_read","input":{}}}""",
        """{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"path\":"}}""",
        """{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"\"a.txt\"}"}}""",
        """{"type":"content_block_stop","index":2}""",
        """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":42}}""",
        """{"type":"message_stop"}""");

    private sealed class ScriptedHandler(params (HttpStatusCode Status, string Body)[] replies) : HttpMessageHandler
    {
        private int _next;
        public List<(string Body, HttpRequestHeaders Headers, Uri Uri)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((await request.Content!.ReadAsStringAsync(ct), request.Headers, request.RequestUri!));
            var (status, body) = replies[Math.Min(_next++, replies.Length - 1)];
            var resp = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8) };
            if (status == HttpStatusCode.OK)
            {
                resp.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            }

            return resp;
        }
    }

    private sealed class NamedTool(string name) : ITool
    {
        public string Name => name;
        public string Description => "d";
        public JsonElement InputSchema => JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"}}}""").RootElement.Clone();
        public bool IsReadOnly => true;
        public bool IsConcurrencySafe => true;
        public IAsyncEnumerable<ToolProgress> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private static AnthropicChatModel Model(ScriptedHandler h)
        => new(new HttpClient(h), "https://api.anthropic.com/v1", "sk-ant-test", "claude-x");

    private static async Task<List<StreamEvent>> Run(IChatModel model, IReadOnlyList<Message> history, IReadOnlyList<ITool>? tools = null)
    {
        var events = new List<StreamEvent>();
        await foreach (var ev in model.StreamAsync(history, tools ?? [new NamedTool("mcp.files.read")], default))
        {
            events.Add(ev);
        }

        return events;
    }

    [Fact]
    public async Task Stream_yields_text_signed_reasoning_and_the_tool_call_under_its_original_name()
    {
        var events = await Run(Model(new ScriptedHandler((HttpStatusCode.OK, ToolTurn))), [new UserMessage("read a.txt")]);

        Assert.Equal("Reading.", string.Concat(events.OfType<TextDelta>().Select(t => t.Text)));
        var reasoning = Assert.Single(events.OfType<ReasoningCaptured>()).Block;
        Assert.Equal("anthropic", reasoning.Provider);
        Assert.Equal("thinking", reasoning.Raw.GetProperty("type").GetString());
        Assert.Equal("plan", reasoning.Raw.GetProperty("thinking").GetString());
        Assert.Equal("SIG==", reasoning.Raw.GetProperty("signature").GetString());

        var call = Assert.Single(events.OfType<ToolCallRequested>()).Block;
        Assert.Equal("mcp.files.read", call.Name);   // 바꿔 보낸 이름을 되돌린다
        Assert.Equal("a.txt", call.Input.GetProperty("path").GetString());

        var done = Assert.IsType<TurnCompleted>(events[^1]);
        Assert.Equal("tool_calls", done.StopReason);
        Assert.Equal(new Usage(105, 42, 90, 5), done.Usage);
    }

    [Fact]
    public async Task Request_carries_headers_system_merged_turns_and_replayed_reasoning()
    {
        Environment.SetEnvironmentVariable("MOAI_REASONING_EFFORT", "high");
        var h = new ScriptedHandler((HttpStatusCode.OK, ToolTurn));
        var thinking = JsonDocument.Parse("""{"type":"thinking","thinking":"","signature":"S1"}""").RootElement.Clone();
        var input = JsonDocument.Parse("""{"path":"a.txt"}""").RootElement.Clone();
        var history = new List<Message>
        {
            new SystemMessage("you are helpful"),
            new UserMessage("read a.txt"),
            new AssistantMessage([
                new ReasoningBlock("anthropic", thinking),
                new ReasoningBlock("other", thinking),
                new TextBlock("Reading."),
                new ToolUseBlock("call:z.ai/1", "mcp.files.read", input),
            ]),
            new ToolResultMessage("call:z.ai/1", "hello"),
            new UserMessage("<reminder>"),
        };

        await Run(Model(h), history);

        var (body, headers, uri) = h.Requests[0];
        Assert.Equal("https://api.anthropic.com/v1/messages", uri.ToString());
        Assert.Equal("sk-ant-test", headers.GetValues("x-api-key").Single());
        Assert.Equal("2026-10-21", headers.GetValues("anthropic-version").Single());
        Assert.Equal("thinking-binding-controls-2026-08-01", headers.GetValues("anthropic-beta").Single());

        var root = JsonDocument.Parse(body).RootElement;
        Assert.Equal("you are helpful", root.GetProperty("system").GetString());
        Assert.Equal("drop_block", root.GetProperty("thinking").GetProperty("block_binding").GetProperty("prefix_mismatch_behavior").GetString());
        Assert.Equal("high", root.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("ephemeral", root.GetProperty("cache_control").GetProperty("type").GetString());
        var tool = root.GetProperty("tools")[0];
        Assert.Equal("mcp_files_read", tool.GetProperty("name").GetString());
        Assert.True(tool.GetProperty("eager_input_streaming").GetBoolean());

        var msgs = root.GetProperty("messages");
        Assert.Equal(3, msgs.GetArrayLength());   // user · assistant · user(툴 결과 + 리마인더 합침)
        var assistant = msgs[1].GetProperty("content");
        Assert.Equal(3, assistant.GetArrayLength());   // 다른 프로바이더 추론은 빠진다: thinking · text · tool_use
        Assert.Equal("S1", assistant[0].GetProperty("signature").GetString());
        Assert.Equal("call_z_ai_1", assistant[2].GetProperty("id").GetString());
        Assert.Equal("mcp_files_read", assistant[2].GetProperty("name").GetString());
        var last = msgs[2].GetProperty("content");
        Assert.Equal("tool_result", last[0].GetProperty("type").GetString());
        Assert.Equal("call_z_ai_1", last[0].GetProperty("tool_use_id").GetString());
        Assert.Equal("<reminder>", last[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Unsupported_setting_and_signature_mismatch_are_retried_without_them()
    {
        var h = new ScriptedHandler(
            (HttpStatusCode.BadRequest, """{"type":"error","error":{"type":"invalid_request_error","message":"thinking.type: adaptive is not supported on this model"}}"""),
            (HttpStatusCode.BadRequest, """{"type":"error","error":{"type":"invalid_request_error","message":"messages.1.content.0: Invalid `signature` in `thinking` block. The block is bound to a different conversation."}}"""),
            (HttpStatusCode.OK, ToolTurn));
        var thinking = JsonDocument.Parse("""{"type":"thinking","thinking":"","signature":"S1"}""").RootElement.Clone();
        var history = new List<Message>
        {
            new UserMessage("hi"),
            new AssistantMessage([new ReasoningBlock("anthropic", thinking), new TextBlock("hello")]),
            new UserMessage("again"),
        };

        await Run(Model(h), history);

        Assert.Equal(3, h.Requests.Count);
        Assert.False(JsonDocument.Parse(h.Requests[1].Body).RootElement.TryGetProperty("thinking", out _));
        Assert.DoesNotContain("\"signature\"", h.Requests[2].Body);
    }

    [Fact]
    public async Task Overloaded_error_event_is_transient_and_a_cut_stream_is_retryable()
    {
        var overloaded = Sse(
            """{"type":"message_start","message":{"usage":{"input_tokens":1,"output_tokens":0}}}""",
            """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""");
        var ex = await Assert.ThrowsAsync<ProviderException>(() => Run(Model(new ScriptedHandler((HttpStatusCode.OK, overloaded))), [new UserMessage("hi")]));
        Assert.Equal(ErrorCategory.Overloaded, ex.Category);
        Assert.True(ex.IsTransient);

        var cut = Sse("""{"type":"message_start","message":{"usage":{"input_tokens":1,"output_tokens":0}}}""");
        var ex2 = await Assert.ThrowsAsync<ProviderException>(() => Run(Model(new ScriptedHandler((HttpStatusCode.OK, cut))), [new UserMessage("hi")]));
        Assert.True(ex2.IsTransient);
    }

    [Fact]
    public async Task Refusal_shows_a_reason_instead_of_an_empty_reply()
    {
        var refusal = Sse(
            """{"type":"message_start","message":{"usage":{"input_tokens":1,"output_tokens":0}}}""",
            """{"type":"message_delta","delta":{"stop_reason":"refusal"},"usage":{"output_tokens":0}}""",
            """{"type":"message_stop"}""");
        var events = await Run(Model(new ScriptedHandler((HttpStatusCode.OK, refusal))), [new UserMessage("hi")]);
        Assert.Equal(L10n.Get("providers.anthropic.refused"), Assert.Single(events.OfType<TextDelta>()).Text);
    }

    [Fact]
    public async Task Engine_stores_reasoning_ahead_of_the_reply_and_other_providers_never_send_it()
    {
        var engine = new QueryEngine(new OneShot(ToolTurnWithoutTools()), []);
        await foreach (var _ in engine.SubmitAsync("hi"))
        {
        }

        var assistant = engine.Messages.OfType<AssistantMessage>().Single();
        Assert.IsType<ReasoningBlock>(assistant.Content[0]);
        Assert.IsType<TextBlock>(assistant.Content[1]);

        // 세션 저장(JSON)으로 왕복해도 남는다.
        var json = JsonSerializer.Serialize<Message>(assistant);
        var back = Assert.IsType<AssistantMessage>(JsonSerializer.Deserialize<Message>(json));
        Assert.Equal("SIG==", Assert.IsType<ReasoningBlock>(back.Content[0]).Raw.GetProperty("signature").GetString());

        // OpenAI 호환 요청에는 추론 블록이 실리지 않는다.
        var h = new ScriptedHandler((HttpStatusCode.OK,
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\",\"index\":0}]}\n\ndata: [DONE]\n\n"));
        await Run(new OpenAiChatModel(new HttpClient(h), "http://h/v1", "", "m", Core.ProviderKind.Vllm), [.. engine.Messages, new UserMessage("next")], []);
        Assert.DoesNotContain("SIG==", h.Requests[0].Body);
    }

    private static string ToolTurnWithoutTools() => Sse(
        """{"type":"message_start","message":{"usage":{"input_tokens":1,"output_tokens":0}}}""",
        """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}""",
        """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"SIG=="}}""",
        """{"type":"content_block_stop","index":0}""",
        """{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}""",
        """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"hello"}}""",
        """{"type":"content_block_stop","index":1}""",
        """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":3}}""",
        """{"type":"message_stop"}""");

    private sealed class OneShot(string sse) : IChatModel
    {
        public IAsyncEnumerable<StreamEvent> StreamAsync(IReadOnlyList<Message> messages, IReadOnlyList<ITool> tools, CancellationToken ct)
            => new AnthropicChatModel(new HttpClient(new ScriptedHandler((HttpStatusCode.OK, sse))), "https://api.anthropic.com/v1", "k", "claude-x")
                .StreamAsync(messages, tools, ct);
    }
}
