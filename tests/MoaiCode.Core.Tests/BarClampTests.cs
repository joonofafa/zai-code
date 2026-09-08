using MoaiCode.Tui;
using Xunit;

namespace MoaiCode.Core.Tests;

public class BarClampTests
{
    [Fact]
    public void Short_text_is_unchanged()
    {
        Assert.Equal("hello", ReplApp.ClampToWidth("hello", 20, keepEnd: false));
    }

    [Fact]
    public void Wide_hangul_hint_never_exceeds_max_cells()
    {
        // 한글(폭 2셀) 힌트가 좁은 폭이면 잘려서 표시폭이 maxCells 를 넘지 않아야 한다(바 wrap 방지).
        const string hint = "입력하면 다음 요청으로 큐잉됩니다.";
        var clamped = ReplApp.ClampToWidth(hint, 10, keepEnd: false);

        Assert.True(LineEditor.DisplayWidth(clamped) <= 10, $"width={LineEditor.DisplayWidth(clamped)}");
        Assert.EndsWith("…", clamped);          // 앞부분 유지 + 말줄임
    }

    [Fact]
    public void KeepEnd_preserves_recent_input_with_leading_ellipsis()
    {
        // 입력줄은 최근(뒤)을 남기고 앞에 말줄임 — 타이핑 중인 끝이 보이게.
        var clamped = ReplApp.ClampToWidth("가나다라마바사아자차", 8, keepEnd: true);

        Assert.True(LineEditor.DisplayWidth(clamped) <= 8);
        Assert.StartsWith("…", clamped);
        Assert.EndsWith("차", clamped);
    }
}
