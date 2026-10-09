using System.Collections.Concurrent;
using MoaiCode.Tui.Input;
using Xunit;

namespace MoaiCode.Core.Tests;

/// <summary>
/// ESC 타임아웃 회귀. 폴링(TryReadEvent) 경로는 ESC 바이트 자신의 신호로 깨어나 대기 없이 ESC 를 확정했다 —
/// 느린 SSH 에서 ESC 와 `[A` 가 따로 도착하면 입력창엔 ESC + 글자 `[A` 가 들어가고, 턴 중엔 화살표가 턴을 취소했다.
/// </summary>
public sealed class TerminalInputEscTests
{
    [Fact]
    public async Task Split_escape_sequence_is_still_one_arrow_key_when_polling()
    {
        var server = new FeedStream();
        using var input = new TerminalInput(server);

        server.Write([0x1B]);
        var first = input.TryReadEvent(0);   // ESC 만 도착 — 아직 확정하면 안 된다
        await Task.Delay(15);
        server.Write("[A"u8);

        var events = new List<InputEvent>();
        if (first is not null)
        {
            events.Add(first);
        }

        var until = DateTime.UtcNow.AddMilliseconds(300);
        while (DateTime.UtcNow < until && events.Count == 0)
        {
            if (input.TryReadEvent(40) is { } ev)
            {
                events.Add(ev);
            }
        }

        var key = Assert.IsType<KeyEvent>(Assert.Single(events));
        Assert.Equal(ConsoleKey.UpArrow, key.Key.Key);
    }

    [Fact]
    public async Task Lone_escape_is_confirmed_after_the_timeout()
    {
        var server = new FeedStream();
        using var input = new TerminalInput(server);

        server.Write([0x1B]);
        await Task.Delay(10);

        InputEvent? ev = null;
        var until = DateTime.UtcNow.AddMilliseconds(500);
        while (DateTime.UtcNow < until && ev is null)
        {
            ev = input.TryReadEvent(40);
        }

        var key = Assert.IsType<KeyEvent>(ev);
        Assert.Equal(ConsoleKey.Escape, key.Key.Key);
    }

    /// <summary>쓴 조각을 그대로 한 번의 Read 로 돌려주는 차단형 스트림(조각 사이 간격 = 도착 간격).</summary>
    private sealed class FeedStream : Stream
    {
        private readonly BlockingCollection<byte[]> _chunks = new();

        public override void Write(byte[] buffer, int offset, int count) => _chunks.Add(buffer[offset..(offset + count)]);

        public override void Write(ReadOnlySpan<byte> buffer) => _chunks.Add(buffer.ToArray());

        public override int Read(byte[] buffer, int offset, int count)
        {
            var chunk = _chunks.Take();
            chunk.CopyTo(buffer, offset);
            return chunk.Length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
