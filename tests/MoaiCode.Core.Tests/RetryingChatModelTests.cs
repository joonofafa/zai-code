using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using MoaiCode.Core.Agent;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;
using MoaiCode.Providers;
using MoaiCode.Providers.OpenAi;
using Xunit;

namespace MoaiCode.Core.Tests;

public class RetryingChatModelTests
{
    /// <summary>호출 순서대로 미리 큐에 담긴 응답을 반환하는 핸들러.</summary>
    private sealed class QueuedHandler(Queue<Func<HttpResponseMessage>> responses) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(responses.Dequeue()());
        }
    }

    private static HttpResponseMessage Error(HttpStatusCode code)
        => new(code) { Content = new StringContent("{\"error\":\"transient\"}") };

    private static HttpResponseMessage Sse(string body)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body))),
        };
        resp.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return resp;
    }

    [Fact]
    public async Task Retries_after_429_then_succeeds()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"index\":0}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\",\"index\":0}]}\n\n" +
            "data: [DONE]\n\n";

        var queue = new Queue<Func<HttpResponseMessage>>();
        queue.Enqueue(() => Error(HttpStatusCode.TooManyRequests)); // 429
        queue.Enqueue(() => Sse(sse));                              // 200
        var handler = new QueuedHandler(queue);

        var inner = new OpenAiChatModel(new HttpClient(handler), "http://t/v1", "k", "m");
        // 즉시 재시도 (테스트에서 지연 제거)
        var model = new RetryingChatModel(inner, maxRetries: 3, delay: (_, _) => Task.CompletedTask);

        var text = "";
        await foreach (var ev in model.StreamAsync(new[] { new UserMessage("hi") }, Array.Empty<ITool>(), default))
        {
            if (ev is TextDelta d)
            {
                text += d.Text;
            }
        }

        Assert.Equal("ok", text);
        Assert.Equal(2, handler.Calls); // 429 → 재시도 → 200
    }

    [Fact]
    public async Task Does_not_retry_on_401_auth_error()
    {
        var queue = new Queue<Func<HttpResponseMessage>>();
        queue.Enqueue(() => Error(HttpStatusCode.Unauthorized)); // 401 (non-transient)
        var handler = new QueuedHandler(queue);

        var inner = new OpenAiChatModel(new HttpClient(handler), "http://t/v1", "k", "m");
        var model = new RetryingChatModel(inner, maxRetries: 3, delay: (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<ProviderException>(async () =>
        {
            await foreach (var _ in model.StreamAsync(new[] { new UserMessage("hi") }, Array.Empty<ITool>(), default))
            {
            }
        });

        Assert.Equal(1, handler.Calls); // 재시도 없음
    }

    [Fact]
    public void Classifies_error_categories()
    {
        Assert.Equal(ErrorCategory.RateLimited, new ProviderException(429, "x").Category);
        Assert.Equal(ErrorCategory.AuthInvalid, new ProviderException(401, "x").Category);
        Assert.Equal(ErrorCategory.ServerError, new ProviderException(503, "x").Category);
        Assert.Equal(ErrorCategory.ContextOverflow, new ProviderException(400, "maximum context length exceeded").Category);
        Assert.True(new ProviderException(529, "x").IsTransient);
        Assert.False(new ProviderException(401, "x").IsTransient);
    }
}
