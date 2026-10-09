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
}
