using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MoaiCode.Core.Agent;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;
using MoaiCode.Providers.OpenAi;
using Xunit;

namespace MoaiCode.Core.Tests;

public class OpenAiChatModelTests
{
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
}
