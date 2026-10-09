using System.Text;
using MoaiCode.Tui.Input;
using Xunit;

namespace MoaiCode.Core.Tests;

/// <summary>대용량 붙여넣기 회귀. 상한(4MB)을 넘은 나머지가 키 입력으로 파싱돼 줄바꿈마다 Enter(제출)가 됐다.</summary>
public sealed class VtParserPasteTests
{
    [Fact]
    public void Oversized_paste_never_leaks_into_keystrokes()
    {
        var parser = new VtParser();
        var events = new List<InputEvent>();
        events.AddRange(parser.Push("\u001b[200~"u8));
        var chunk = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(new string('a', 1023) + "\r", 1024)));   // 1MB, 줄바꿈 포함
        for (var i = 0; i < 5; i++)
        {
            events.AddRange(parser.Push(chunk));
        }

        events.AddRange(parser.Push("\u001b[201~x"u8));

        Assert.Single(events.OfType<PasteEvent>());
        var keys = events.OfType<KeyEvent>().ToList();
        Assert.Equal('x', Assert.Single(keys).Key.KeyChar);   // 붙여넣기 뒤에 친 글자만 키로 나온다
    }

    // ESC 뒤 바이트를 무조건 Alt+글자로 만들어, ESC 두 번·ESC 직후 한글·ESC ESC [A 가 깨졌다.
    [Fact]
    public void Escape_followed_by_escape_or_multibyte_is_not_an_alt_combo()
    {
        var parser = new VtParser();
        var evs = parser.Push("\u001b\u001b[A"u8).Concat(parser.Flush()).OfType<KeyEvent>().ToList();
        Assert.Equal(new[] { ConsoleKey.Escape, ConsoleKey.UpArrow }, evs.Select(e => e.Key.Key));

        parser = new VtParser();
        evs = parser.Push(Encoding.UTF8.GetBytes("\u001b한")).Concat(parser.Flush()).OfType<KeyEvent>().ToList();
        Assert.Equal(ConsoleKey.Escape, evs[0].Key.Key);
        Assert.Equal('한', evs[1].Key.KeyChar);

        parser = new VtParser();
        evs = parser.Push("\u001bx"u8).OfType<KeyEvent>().ToList();   // 일반 Alt+x 는 그대로
        Assert.True(Assert.Single(evs).Key.Modifiers.HasFlag(ConsoleModifiers.Alt));
    }
}
