using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MoaiCode.Core.Agent;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;
using MoaiCode.Providers.OpenAi;
using Xunit;

namespace MoaiCode.Core.Tests;

[Collection("EnvMutating")]
public class OpenAiChatModelTests
{
    private sealed class CaptureRequestHandler(string sse) : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(sse))),
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return resp;
        }
    }

    private sealed class FakeSseHandler(string sse) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(sse))),
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(resp);
        }
    }

    private static async Task<List<StreamEvent>> Collect(string sse, IReadOnlyList<ITool>? tools = null)
    {
        var http = new HttpClient(new FakeSseHandler(sse));
        var model = new OpenAiChatModel(http, "http://test/v1", "key", "gpt-test");
        var history = new List<Message> { new UserMessage("hi") };

        var events = new List<StreamEvent>();
        await foreach (var ev in model.StreamAsync(history, tools ?? Array.Empty<ITool>(), default))
        {
            events.Add(ev);
        }

        return events;
    }

    private static async Task<string?> CaptureRequestBodyAsync(string sse)
    {
        var handler = new CaptureRequestHandler(sse);
        var http = new HttpClient(handler);
        var model = new OpenAiChatModel(http, "http://test/v1", "key", "gpt-test");
        var history = new List<Message> { new UserMessage("hi") };

        await foreach (var _ in model.StreamAsync(history, Array.Empty<ITool>(), default))
        {
        }

        return handler.LastRequestBody;
    }

    [Fact]
    public async Task Parses_text_deltas_usage_and_stop_reason()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"Hello\"},\"index\":0}]}\n" +
            "\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\" world\"},\"index\":0}]}\n" +
            "\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\",\"index\":0}]}\n" +
            "\n" +
            "data: {\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2},\"choices\":[]}\n" +
            "\n" +
            "data: [DONE]\n\n";

        var events = await Collect(sse);

        var text = string.Concat(events.OfType<TextDelta>().Select(d => d.Text));
        var completed = events.OfType<TurnCompleted>().Single();

        Assert.Equal("Hello world", text);
        Assert.Equal("stop", completed.StopReason);
        Assert.Equal(10, completed.Usage.InputTokens);
        Assert.Equal(2, completed.Usage.OutputTokens);
    }

    [Fact]
    public async Task Accumulates_tool_call_across_argument_chunks()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"Bash\",\"arguments\":\"{\\\"comm\"}}]},\"index\":0}]}\n" +
            "\n" +
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"and\\\":\\\"ls\\\"}\"}}]},\"index\":0}]}\n" +
            "\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\",\"index\":0}]}\n" +
            "\n" +
            "data: [DONE]\n\n";

        var events = await Collect(sse);

        var call = events.OfType<ToolCallRequested>().Single();
        Assert.Equal("call_1", call.Block.Id);
        Assert.Equal("Bash", call.Block.Name);
        Assert.Equal("ls", call.Block.Input.GetProperty("command").GetString());
        Assert.Equal("tool_calls", events.OfType<TurnCompleted>().Single().StopReason);
    }

    [Fact]
    public async Task Reasoning_only_response_falls_back_to_reasoning_text()
    {
        // 추론 모델이 content 없이 reasoning_content 만 흘리는 경우 → 빈 응답 대신 reasoning 을 답변으로.
        var sse =
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"reasoning_content\":\"곰곰이 \"},\"index\":0}]}\n" +
            "\n" +
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"생각한 결과\"},\"index\":0}]}\n" +
            "\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\",\"index\":0}]}\n" +
            "\n" +
            "data: [DONE]\n\n";

        var events = await Collect(sse);
        var text = string.Concat(events.OfType<TextDelta>().Select(d => d.Text));
        Assert.Equal("곰곰이 생각한 결과", text);
    }

    [Fact]
    public async Task Error_in_sse_body_surfaces_as_exception()
    {
        // 게이트웨이가 200 + SSE 본문에 error 를 담아 보내면(모델 비활성 등) 빈 응답으로 삼키지 말고 예외.
        var sse =
            "data: {\"error\":{\"message\":\"model is disabled\",\"type\":\"invalid_request_error\",\"code\":403}}\n" +
            "\n" +
            "data: [DONE]\n\n";

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Collect(sse));
        Assert.Contains("model is disabled", ex.Message);
    }

    [Fact]
    public async Task Error_as_plain_string_surfaces()
    {
        var sse = "data: {\"error\":\"upstream unavailable\"}\n\ndata: [DONE]\n\n";
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Collect(sse));
        Assert.Contains("upstream unavailable", ex.Message);
    }

    [Fact]
    public async Task Content_present_suppresses_reasoning_fallback()
    {
        // content 가 있으면 reasoning 은 방출하지 않는다(생각 과정은 감춤).
        var sse =
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"internal thinking\"},\"index\":0}]}\n" +
            "\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"final answer\"},\"index\":0}]}\n" +
            "\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\",\"index\":0}]}\n" +
            "\n" +
            "data: [DONE]\n\n";

        var events = await Collect(sse);
        var text = string.Concat(events.OfType<TextDelta>().Select(d => d.Text));
        Assert.Equal("final answer", text);
        Assert.DoesNotContain("thinking", text);
    }

    [Fact]
    public async Task Includes_reasoning_effort_when_env_is_set()
    {
        var prev = Environment.GetEnvironmentVariable("MOAI_REASONING_EFFORT");
        try
        {
            Environment.SetEnvironmentVariable("MOAI_REASONING_EFFORT", "high");

            var body = await CaptureRequestBodyAsync(
                "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\",\"index\":0}]}\n\n" +
                "data: [DONE]\n\n");

            Assert.NotNull(body);
            Assert.Contains("\"reasoning_effort\":\"high\"", body, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MOAI_REASONING_EFFORT", prev);
        }
    }

    [Fact]
    public async Task Omits_reasoning_effort_when_value_is_invalid()
    {
        var prev = Environment.GetEnvironmentVariable("MOAI_REASONING_EFFORT");
        try
        {
            Environment.SetEnvironmentVariable("MOAI_REASONING_EFFORT", "max");

            var body = await CaptureRequestBodyAsync(
                "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\",\"index\":0}]}\n\n" +
                "data: [DONE]\n\n");

            Assert.NotNull(body);
            Assert.DoesNotContain("\"reasoning_effort\"", body, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MOAI_REASONING_EFFORT", prev);
        }
    }
}
