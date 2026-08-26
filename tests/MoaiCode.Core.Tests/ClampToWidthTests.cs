using MoaiCode.Tui;
using Xunit;

namespace MoaiCode.Core.Tests;

// ReplApp.ClampToWidth — 하단 입력바 본문 클램프 검증.
// 좁은 폭에서 한글(와이드문자=2셀) 힌트/입력이 바 한 행을 넘어 wrap → 스크롤 영역으로
// 넘쳐 잔상으로 남던 회귀를 막는다. 폭 계산은 LineEditor.DisplayWidth(표시폭) 기준.
public class ClampToWidthTests
{
    // repl.typeahead.hint 와 동일 구성의 한글 문자열(전각 혼합 — 마침표만 1셀).
    private const string KoreanHint = "입력하면 다음 요청으로 큐잉됩니다.";

    [Fact]
    public void Hint_keeps_front_and_appends_ellipsis_within_maxCells()
    {
        var clamped = ReplApp.ClampToWidth(KoreanHint, 10, tailEllipsis: true);

        Assert.True(LineEditor.DisplayWidth(clamped) <= 10, $"width={LineEditor.DisplayWidth(clamped)}");
        Assert.StartsWith("입력", clamped);          // 안내문 앞쪽 유지
        Assert.EndsWith("\u2026", clamped);          // 뒤 …
    }

    [Fact]
    public void Hint_short_enough_returned_unchanged()
    {
        var clamped = ReplApp.ClampToWidth("abc", 10, tailEllipsis: true);
        Assert.Equal("abc", clamped);
    }

    [Fact]
    public void InputLine_keeps_tail_and_prepends_ellipsis_within_maxCells()
    {
        var line = "이건 매우 긴 입력줄 — Glob scripts/*.py 실행";

        var clamped = ReplApp.ClampToWidth(line, 12, tailEllipsis: false);

        Assert.True(LineEditor.DisplayWidth(clamped) <= 12, $"width={LineEditor.DisplayWidth(clamped)}");
        Assert.StartsWith("\u2026", clamped);                 // 앞 …
        Assert.EndsWith("실행", clamped);                     // 최근 입력(뒤쪽) 유지
        Assert.DoesNotContain("Glob", clamped);               // 잘린 앞쪽은 노출 안 함
    }

    [Fact]
    public void Wide_char_at_boundary_is_not_split()
    {
        // 폭 7("한글"4 + "abc"3) 을 maxCells 5 로 — 뒤에서 소비 시 '글'(2셀)이 경계에 걸려
        // 중간에 잘리지 않고 앞 … 로 대체돼야 한다("…abc", 폭 4 ≤ 5).
        var clamped = ReplApp.ClampToWidth("한글abc", 5, tailEllipsis: false);

        Assert.Equal("\u2026abc", clamped);
        Assert.True(LineEditor.DisplayWidth(clamped) <= 5);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(31)]   // 원본 전체 폭과 동일 → 변경 없음
    public void Hint_width_never_exceeds_maxCells(int maxCells)
    {
        var clamped = ReplApp.ClampToWidth(KoreanHint, maxCells, tailEllipsis: true);
        Assert.True(LineEditor.DisplayWidth(clamped) <= maxCells);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(30)]
    public void InputLine_width_never_exceeds_maxCells(int maxCells)
    {
        var line = "가나다라마바사아자차카타파하";
        var clamped = ReplApp.ClampToWidth(line, maxCells, tailEllipsis: false);
        Assert.True(LineEditor.DisplayWidth(clamped) <= maxCells);
    }

    [Fact]
    public void Empty_input_returns_empty()
    {
        Assert.Equal(string.Empty, ReplApp.ClampToWidth(string.Empty, 5, tailEllipsis: true));
        Assert.Equal(string.Empty, ReplApp.ClampToWidth(string.Empty, 5, tailEllipsis: false));
    }

    [Fact]
    public void Defensive_maxCells_below_one_returns_empty()
    {
        Assert.Equal(string.Empty, ReplApp.ClampToWidth(KoreanHint, 0, tailEllipsis: true));
        Assert.Equal(string.Empty, ReplApp.ClampToWidth(KoreanHint, -3, tailEllipsis: false));
    }
}
