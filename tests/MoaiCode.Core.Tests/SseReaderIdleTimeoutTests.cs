using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MoaiCode.Core.Agent;
using MoaiCode.Core.Messages;
using MoaiCode.Providers;
using MoaiCode.Providers.Http;
using Xunit;

namespace MoaiCode.Core.Tests;

// SseReader idle-read 타임아웃: 연결이 살아있는 채 데이터가 안 오는 silent stall 을
// 무한 대기하지 않고 NetworkTransient 로 변환하는지 검증.
[Collection("EnvMutating")]
public class SseReaderIdleTimeoutTests
{
    // 첫 data 줄 하나만 주고 이후 영원히 굳어버리는 스트림(세미콜론 주석도 없음 — 진짜 침묵).
    private sealed class StallingStream : Stream
    {
        private readonly byte[] _head;
        private bool _delivered;

        public StallingStream(string head) => _head = Encoding.UTF8.GetBytes(head);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            if (!_delivered)
            {
                _delivered = true;
                _head.AsMemory(0, _head.Length).CopyTo(buffer);
                return _head.Length;
            }

            // 데이터 없이 연결 유지 — silent stall. 호출자가 취소할 때까지 대기.
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<List<string>> CollectDataLinesAsync(Stream stream, CancellationToken ct = default)
    {
        var lines = new List<string>();
        await foreach (var line in SseReader.ReadDataLinesAsync(stream, ct).ConfigureAwait(false))
        {
            lines.Add(line);
        }
        return lines;
    }

    // 침묵 스트림 → idle timeout 이 ProviderException(NetworkTransient) 으로 발동해야 한다.
    [Fact]
    public async Task Silent_stall_raises_transient_provider_exception()
    {
        var prev = Environment.GetEnvironmentVariable("MOAI_STREAM_IDLE_TIMEOUT_SECONDS");
        try
        {
            Environment.SetEnvironmentVariable("MOAI_STREAM_IDLE_TIMEOUT_SECONDS", "10"); // 클램프 하한

            using var stream = new StallingStream("data: {\"ok\":1}\n\n");
            var ex = await Assert.ThrowsAsync<ProviderException>(
                () => CollectDataLinesAsync(stream));
            Assert.Equal(ErrorCategory.NetworkTransient, ex.Category);
            Assert.True(ex.IsTransient);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MOAI_STREAM_IDLE_TIMEOUT_SECONDS", prev);
        }
    }

    // 정상 종료 스트림([DONE] 전에 EOF)은 타임아웃 없이 그대로 완료 — false positive 방지.
    [Fact]
    public async Task Completed_stream_is_not_affected()
    {
        var prev = Environment.GetEnvironmentVariable("MOAI_STREAM_IDLE_TIMEOUT_SECONDS");
        try
        {
            Environment.SetEnvironmentVariable("MOAI_STREAM_IDLE_TIMEOUT_SECONDS", "10");

            using var normal = new MemoryStream(Encoding.UTF8.GetBytes(
                "data: {\"a\":1}\n\ndata: {\"b\":2}\n\ndata: [DONE]\n\n"));
            var lines = await CollectDataLinesAsync(normal);
            Assert.Equal(3, lines.Count);
            Assert.Equal("{\"a\":1}", lines[0]);
            Assert.Equal("[DONE]", lines[2]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MOAI_STREAM_IDLE_TIMEOUT_SECONDS", prev);
        }
    }

    // keepalive 주석(': ping')만 계속 오는 스트림: 연결은 살아있지만 실제 data 가 없으면
    // idle 타임아웃이 발동해야 한다(예전엔 주석 줄도 '한 줄 도착'으로 리셋돼 무한 대기했다).
    [Fact]
    public async Task Keepalive_comments_do_not_reset_idle_timer()
    {
        var prev = Environment.GetEnvironmentVariable("MOAI_STREAM_IDLE_TIMEOUT_SECONDS");
        try
        {
            Environment.SetEnvironmentVariable("MOAI_STREAM_IDLE_TIMEOUT_SECONDS", "10");

            // data 1줄 → 이후 30초 간격 주석만 무한히(테스트 창 내엔 도달하지 않음).
            // 주석이 매 10초 안으로 와도 data 기준 타이머는 리셋되지 않아야 한다.
            using var stream = new KeepaliveStallingStream("data: {\"ok\":1}\n\n");
            var ex = await Assert.ThrowsAsync<ProviderException>(
                () => CollectDataLinesAsync(stream));
            Assert.Equal(ErrorCategory.NetworkTransient, ex.Category);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MOAI_STREAM_IDLE_TIMEOUT_SECONDS", prev);
        }
    }

    // data 줄 사이 사이에 주석이 끼어들어도 정상 종료엔 영향 없음(false positive 방지).
    [Fact]
    public async Task Interleaved_keepalive_with_data_completes_normally()
    {
        var prev = Environment.GetEnvironmentVariable("MOAI_STREAM_IDLE_TIMEOUT_SECONDS");
        try
        {
            Environment.SetEnvironmentVariable("MOAI_STREAM_IDLE_TIMEOUT_SECONDS", "10");

            using var normal = new MemoryStream(Encoding.UTF8.GetBytes(
                ": ping\n\ndata: {\"a\":1}\n\n: ping\n\ndata: [DONE]\n\n"));
            var lines = await CollectDataLinesAsync(normal);
            Assert.Equal(2, lines.Count);
            Assert.Equal("{\"a\":1}", lines[0]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MOAI_STREAM_IDLE_TIMEOUT_SECONDS", prev);
        }
    }

    // 첫 data 1줄 후 주석 keepalive 를 짧은 간격으로 계속 밀어주는 스트림 —
    // 연결은 '살아있'으므로 IsNetworkFailure 쪽이 아니라 data-기반 idle 타임아웃이 걸려야 한다.
    private sealed class KeepaliveStallingStream : Stream
    {
        private readonly byte[] _head;
        private bool _delivered;

        public KeepaliveStallingStream(string head) => _head = Encoding.UTF8.GetBytes(head);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            if (!_delivered)
            {
                _delivered = true;
                _head.AsMemory(0, _head.Length).CopyTo(buffer);
                return _head.Length;
            }

            // 주석 keepalive 1회만 흉내(그 후엔 영구 침묵) — 타이머가 data 기준이라면
            // 주석 도착과 무관하게 타임아웃이 발동한다.
            await Task.Delay(200, ct).ConfigureAwait(false);
            ": ping\n\n"u8.ToArray().AsMemory().CopyTo(buffer);
            return 7;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // 호출자 취소는 idle timeout exception 이 아니라 원래의 OperationCanceledException 그대로 전파.
    [Fact]
    public async Task Caller_cancellation_propagates_as_operation_canceled()
    {
        var prev = Environment.GetEnvironmentVariable("MOAI_STREAM_IDLE_TIMEOUT_SECONDS");
        try
        {
            Environment.SetEnvironmentVariable("MOAI_STREAM_IDLE_TIMEOUT_SECONDS", "3600"); // idle 발동 안 하게 충분히 김

            using var cts = new CancellationTokenSource(300);
            using var stream = new StallingStream("data: {\"ok\":1}\n\n");

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => CollectDataLinesAsync(stream, cts.Token));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MOAI_STREAM_IDLE_TIMEOUT_SECONDS", prev);
        }
    }
}
