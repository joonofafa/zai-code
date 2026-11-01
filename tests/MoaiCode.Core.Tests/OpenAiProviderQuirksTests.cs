using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MoaiCode.Core.Agent;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;
using MoaiCode.Providers;
using MoaiCode.Providers.OpenAi;
using Xunit;

namespace MoaiCode.Core.Tests;

/// <summary>
/// OpenAI 호환 4종(Z.ai·OpenAI·OpenRouter·vLLM)의 요청 형식 차이. 고정 max_tokens 는 모델마다 한도가 달라 400 이 나고
/// (vLLM 은 남은 컨텍스트보다 크면 거부), z.ai 전용 필드는 다른 서버가 모르는 필드로 거부한다.
/// </summary>
[Collection("EnvMutating")]
public sealed class OpenAiProviderQuirksTests : IDisposable
{
    private const string Ok =
        "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"},\"index\":0}]}\n\n" +
        "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\",\"index\":0}]}\n\n" +
        "data: [DONE]\n\n";

    private readonly string? _effort = Environment.GetEnvironmentVariable("MOAI_REASONING_EFFORT");
    private readonly string? _max = Environment.GetEnvironmentVariable("MOAI_MAX_TOKENS");

    public OpenAiProviderQuirksTests()
    {
        Environment.SetEnvironmentVariable("MOAI_REASONING_EFFORT", null);
        Environment.SetEnvironmentVariable("MOAI_MAX_TOKENS", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("MOAI_REASONING_EFFORT", _effort);
        Environment.SetEnvironmentVariable("MOAI_MAX_TOKENS", _max);
    }

    /// <summary>응답을 차례로 돌려주고 요청(본문·헤더)을 모두 기록한다. (상태, 본문) 쌍.</summary>
    private sealed class ScriptedHandler(params (HttpStatusCode Status, string Body)[] replies) : HttpMessageHandler
    {
        private int _next;
        public List<(string Body, HttpRequestHeaders Headers)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((await request.Content!.ReadAsStringAsync(ct), request.Headers));
            var (status, body) = replies[Math.Min(_next++, replies.Length - 1)];
            var resp = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8) };
            if (status == HttpStatusCode.OK)
            {
                resp.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            }

            return resp;
        }
    }

    private sealed class Tool : ITool
    {
        public string Name => "t";
        public string Description => "t";
        public JsonElement InputSchema => JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();
        public bool IsReadOnly => true;
        public bool IsConcurrencySafe => true;
        public IAsyncEnumerable<ToolProgress> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private static async Task Run(OpenAiChatModel model)
    {
        await foreach (var _ in model.StreamAsync([new UserMessage("hi")], [new Tool()], default))
        {
        }
    }

    private static JsonElement Body(ScriptedHandler h, int i) => JsonDocument.Parse(h.Requests[i].Body).RootElement;

    [Fact]
    public async Task OpenAI_omits_the_output_limit_unless_set_and_then_uses_max_completion_tokens()
    {
        var h = new ScriptedHandler((HttpStatusCode.OK, Ok));
        var model = new OpenAiChatModel(new HttpClient(h), "https://api.openai.com/v1", "sk-x", "glm-5.3", ProviderKind.OpenAI);
        await Run(model);

        var first = Body(h, 0);
        Assert.False(first.TryGetProperty("max_tokens", out _));
        Assert.False(first.TryGetProperty("max_completion_tokens", out _));
        Assert.False(first.TryGetProperty("tool_stream", out _));   // 모델 이름이 glm 이어도 z.ai 가 아니면 안 보낸다
        Assert.True(first.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());

        Environment.SetEnvironmentVariable("MOAI_MAX_TOKENS", "4096");
        await Run(model);
        Assert.Equal(4096, Body(h, 1).GetProperty("max_completion_tokens").GetInt32());
        Assert.False(Body(h, 1).TryGetProperty("max_tokens", out _));
    }

    [Fact]
    public async Task OpenRouter_sends_effort_in_its_reasoning_object()
    {
        Environment.SetEnvironmentVariable("MOAI_REASONING_EFFORT", "low");
        var h = new ScriptedHandler((HttpStatusCode.OK, Ok));
        await Run(new OpenAiChatModel(new HttpClient(h), "https://openrouter.ai/api/v1", "sk-or", "x/y", ProviderKind.OpenRouter));

        var body = Body(h, 0);
        Assert.Equal("low", body.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.False(body.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task Zai_keeps_its_large_output_limit_and_no_usage_request()
    {
        var h = new ScriptedHandler((HttpStatusCode.OK, Ok));
        await Run(new OpenAiChatModel(new HttpClient(h), "https://api.z.ai/api/coding/paas/v4", "k", "glm-5.3"));

        var body = Body(h, 0);
        Assert.Equal(65_536, body.GetProperty("max_tokens").GetInt32());
        Assert.True(body.GetProperty("tool_stream").GetBoolean());
        Assert.False(body.TryGetProperty("stream_options", out _));
    }

    [Fact]
    public async Task Vllm_without_a_key_sends_no_authorization_and_drops_a_rejected_field()
    {
        Environment.SetEnvironmentVariable("MOAI_REASONING_EFFORT", "high");
        var h = new ScriptedHandler(
            (HttpStatusCode.BadRequest, """{"object":"error","message":"1 validation error: reasoning_effort Extra inputs are not permitted"}"""),
            (HttpStatusCode.OK, Ok),
            (HttpStatusCode.OK, Ok),
            (HttpStatusCode.OK, Ok));
        var model = new OpenAiChatModel(new HttpClient(h), "http://h:8000/v1", "", "qwen", ProviderKind.Vllm);

        await Run(model);
        Assert.Null(h.Requests[0].Headers.Authorization);
        Assert.True(Body(h, 0).TryGetProperty("reasoning_effort", out _));
        Assert.False(Body(h, 1).TryGetProperty("reasoning_effort", out _));   // 빼고 다시 보냈다

        await Run(model);
        Assert.False(Body(h, 2).TryGetProperty("reasoning_effort", out _));   // 같은 모델엔 처음부터 뺀다

        model.CurrentModel = "other";
        await Run(model);
        Assert.True(Body(h, 3).TryGetProperty("reasoning_effort", out _));    // 다른 모델은 다시 시도
    }

    [Fact]
    public async Task Other_400s_are_still_errors()
    {
        var h = new ScriptedHandler((HttpStatusCode.BadRequest, """{"error":{"message":"messages: too long"}}"""));
        var model = new OpenAiChatModel(new HttpClient(h), "http://h:8000/v1", "", "qwen", ProviderKind.Vllm);

        var ex = await Assert.ThrowsAsync<ProviderException>(() => Run(model));
        Assert.Contains("too long", ex.Message);
        Assert.Single(h.Requests);
    }
}
