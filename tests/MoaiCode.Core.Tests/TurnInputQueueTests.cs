using System.Reflection;
using Xunit;

namespace MoaiCode.Core.Tests;

// 타입어헤드 큐: 턴 중 친 입력 중 '엔터로 확정된 것'만 제출 대상이고,
// 치다 만 줄은 제출되지 않고 다음 프롬프트로 되살아나야 한다(TakePartial).
public sealed class TurnInputQueueTests
{
    private static readonly Type QueueType =
        Assembly.Load("MoaiCode.Tui").GetType("MoaiCode.Tui.TurnInputQueue")!;

    private sealed class Q
    {
        private readonly object _q = Activator.CreateInstance(QueueType, nonPublic: true)!;

        public void Type(string s)
        {
            var feed = QueueType.GetMethod("Feed")!;
            foreach (var c in s)
            {
                feed.Invoke(_q, new object[] { new ConsoleKeyInfo(c, ConsoleKey.A, false, false, false) });
            }
        }

        public void Enter() => QueueType.GetMethod("Feed")!
            .Invoke(_q, new object[] { new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false) });

        public void Backspace() => QueueType.GetMethod("Feed")!
            .Invoke(_q, new object[] { new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false) });

        public int Count => (int)QueueType.GetProperty("Count")!.GetValue(_q)!;

        public string Current => (string)QueueType.GetProperty("CurrentLine")!.GetValue(_q)!;

        public string TakePartial() => (string)QueueType.GetMethod("TakePartial")!.Invoke(_q, null)!;

        public string? Dequeue()
        {
            var args = new object?[] { null };
            var ok = (bool)QueueType.GetMethod("TryDequeue")!.Invoke(_q, args)!;
            return ok ? (string?)args[0] : null;
        }
    }

    [Fact]
    public void Unconfirmed_line_is_not_queued_and_comes_back_for_the_prompt()
    {
        var q = new Q();
        q.Type("치던 중");

        // 엔터를 안 쳤으므로 제출 대상이 아니다.
        Assert.Equal(0, q.Count);
        Assert.Null(q.Dequeue());

        // 프롬프트로 되살릴 수 있어야 한다(그리고 큐에서 비워진다).
        Assert.Equal("치던 중", q.TakePartial());
        Assert.Equal("", q.Current);
        Assert.Equal("", q.TakePartial());
    }

    [Fact]
    public void Confirmed_lines_are_queued_while_the_unconfirmed_tail_survives()
    {
        var q = new Q();
        q.Type("first");
        q.Enter();
        q.Type("second");
        q.Enter();
        q.Type("아직 치는 중");

        // 확정된 두 줄만 순서대로 제출된다.
        Assert.Equal(2, q.Count);
        Assert.Equal("first", q.Dequeue());
        Assert.Equal("second", q.Dequeue());
        Assert.Null(q.Dequeue());

        // 치던 줄은 제출되지 않고 남아 있다.
        Assert.Equal("아직 치는 중", q.TakePartial());
    }

    [Fact]
    public void Backspace_edits_the_unconfirmed_line()
    {
        var q = new Q();
        q.Type("abc");
        q.Backspace();
        Assert.Equal("ab", q.TakePartial());
    }
}
