using MoaiCode.Tui;
using Xunit;

namespace MoaiCode.Core.Tests;

// LineEditor 의 wrap 레이아웃 계산(물리 행 수 / 커서 행·열) 검증.
// 입력이 터미널 폭을 넘어 접힐 때 재그리기가 깨지던 회귀를 막는다.
public class LineEditorLayoutTests
{
    [Theory]
    [InlineData(0, 10, 1)]    // 빈 내용도 최소 1행
    [InlineData(9, 10, 1)]    // 폭 미만 → 1행
    [InlineData(10, 10, 1)]   // 폭 정확히 채움 → 여전히 1행(커서는 exact-fill 보정으로 처리)
    [InlineData(11, 10, 2)]   // 폭 초과 1칸 → 2행
    [InlineData(20, 10, 2)]   // 정확히 2행
    [InlineData(21, 10, 3)]   // 2행 초과 → 3행
    public void RowCount_counts_wrapped_physical_rows(int total, int cols, int expected)
        => Assert.Equal(expected, LineEditor.RowCount(total, cols));

    [Theory]
    [InlineData(0, 10, 1)]    // 시작점은 1행
    [InlineData(9, 10, 1)]    // 첫 행 안
    [InlineData(10, 10, 2)]   // 폭 경계: 다음 행 0열(1-기반 2행) — exact-fill
    [InlineData(11, 10, 2)]   // 둘째 행
    [InlineData(15, 10, 2)]   // 둘째 행 중간
    [InlineData(20, 10, 3)]   // 둘째 행 끝 경계
    public void RowOf_returns_one_based_cursor_row(int offset, int cols, int expected)
        => Assert.Equal(expected, LineEditor.RowOf(offset, cols));

    [Theory]
    [InlineData(0, 10, 0)]
    [InlineData(5, 10, 5)]
    [InlineData(10, 10, 0)]   // 경계는 0열
    [InlineData(13, 10, 3)]
    public void ColOf_returns_zero_based_column(int offset, int cols, int expected)
        => Assert.Equal(expected, LineEditor.ColOf(offset, cols));

    [Theory]
    [InlineData(5, 0)]        // cols<1 은 1로 클램프 → 방어적
    [InlineData(5, -3)]
    public void Invalid_cols_is_clamped_not_thrown(int total, int cols)
    {
        Assert.True(LineEditor.RowCount(total, cols) >= 1);
        Assert.True(LineEditor.RowOf(total, cols) >= 1);
        Assert.True(LineEditor.ColOf(total, cols) >= 0);
    }
}
