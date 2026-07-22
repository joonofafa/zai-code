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

    /// <summary>data 바이트를 다 준 뒤 읽기에서 IOException("응답 조기 종료") 을 던지는 스트림.</summary>
    private sealed class FlakyStream(byte[] data) : Stream
    {
        private int _pos;

        private int Serve(Span<byte> dst)
        {
            if (_pos >= data.Length)
            {
                throw new IOException("The response ended prematurely.");
            }

            var n = Math.Min(dst.Length, data.Length - _pos);
            data.AsSpan(_pos, n).CopyTo(dst);
            _pos += n;
            return n;
        }

        public override int Read(byte[] b, int o, int c) => Serve(b.AsSpan(o, c));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => ValueTask.FromResult(Serve(buffer.Span));

        public override Task<int> ReadAsync(byte[] b, int o, int c, CancellationToken ct)
            => Task.FromResult(Serve(b.AsSpan(o, c)));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set { } }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) { }
        public override void Write(byte[] b, int o, int c) { }
    }

    private static HttpResponseMessage Flaky(byte[] data)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FlakyStream(data)) };
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
    public async Task Retries_after_stream_cut_before_any_output()
    {
        var good =
            "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"index\":0}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\",\"index\":0}]}\n\n" +
            "data: [DONE]\n\n";

        var queue = new Queue<Func<HttpResponseMessage>>();
        queue.Enqueue(() => Flaky(Array.Empty<byte>())); // 방출 전 스트림 끊김(ResponseEnded)
        queue.Enqueue(() => Sse(good));                  // 재시도 → 성공
        var handler = new QueuedHandler(queue);

        var inner = new OpenAiChatModel(new HttpClient(handler), "http://t/v1", "k", "m");
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
        Assert.Equal(2, handler.Calls); // 끊김 → 재시도 → 성공
    }

    [Fact]
    public async Task Does_not_retry_after_partial_output_cut()
    {
        // 델타 하나 방출 후 끊김 → 부분출력 중복 방지 위해 재시도하지 않고 예외 전파.
        var partial = Encoding.UTF8.GetBytes(
            "data: {\"choices\":[{\"delta\":{\"content\":\"par\"},\"index\":0}]}\n\n");

        var queue = new Queue<Func<HttpResponseMessage>>();
        queue.Enqueue(() => Flaky(partial));
        var handler = new QueuedHandler(queue);

        var inner = new OpenAiChatModel(new HttpClient(handler), "http://t/v1", "k", "m");
        var model = new RetryingChatModel(inner, maxRetries: 3, delay: (_, _) => Task.CompletedTask);

        var text = "";
        var threw = false;
        try
        {
            await foreach (var ev in model.StreamAsync(new[] { new UserMessage("hi") }, Array.Empty<ITool>(), default))
            {
                if (ev is TextDelta d)
                {
                    text += d.Text;
                }
            }
        }
        catch (ProviderException)
        {
            threw = true;
        }

        Assert.Equal("par", text);      // 부분 출력은 이미 방출됨
        Assert.True(threw);             // 재시도 없이 transient 예외 전파
        Assert.Equal(1, handler.Calls); // 재시도 안 함(중복 방지)
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

        // 네트워크 끊김류 → transient 로 분류(재시도 가능).
        Assert.True(new ProviderException("cut", ErrorCategory.NetworkTransient).IsTransient);
        Assert.True(ProviderException.IsNetworkFailure(new IOException("The response ended prematurely.")));
        Assert.True(ProviderException.IsNetworkFailure(new HttpRequestException("connection reset")));
        Assert.False(ProviderException.IsNetworkFailure(new OperationCanceledException()));
    }
}
