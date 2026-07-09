using System.Text;
using MoaiCode.Tui;
using Xunit;

namespace MoaiCode.Core.Tests;

/// <summary>
/// 붙여넣기 표식 회귀 테스트. 예전엔 bracketed paste 를 켜지 않아 붙여넣은 CRLF 가 Enter 로 처리돼
/// 줄마다 전송됐다. 지금은 여러 줄 붙여넣기를 한 줄 표식으로 접고, 전송 직전에 원문으로 되돌린다.
/// </summary>
public sealed class PasteStoreTests
{
    private static (string Buf, int Pos) Insert(string initial, int pos, string pasted)
    {
        var sb = new StringBuilder(initial);
        PasteStore.Insert(sb, ref pos, pasted);
        return (sb.ToString(), pos);
    }

    [Fact]
    public void Single_line_paste_is_inserted_verbatim()
    {
        var (buf, pos) = Insert("", 0, "hello world");

        Assert.Equal("hello world", buf);
        Assert.Equal(11, pos);
    }

    [Fact]
    public void Trailing_newline_does_not_make_it_multiline()
    {
        // 터미널에서 한 줄만 복사하면 끝에 개행이 붙어 온다 — 표식으로 접으면 안 된다.
        var (buf, _) = Insert("", 0, "single line\n");

        Assert.Equal("single line", buf);
    }

    [Fact]
    public void Multiline_paste_is_folded_into_a_placeholder()
    {
        var (buf, pos) = Insert("이 로그 분석해줘 ", 10, "line1\nline2\nline3");

        Assert.StartsWith("이 로그 분석해줘 [붙여넣기 #", buf, StringComparison.Ordinal);
        Assert.Contains("· 3줄]", buf, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", buf, StringComparison.Ordinal); // 입력창은 항상 한 줄
        Assert.Equal(buf.Length, pos);
    }

    [Fact]
    public void Crlf_is_normalized_before_counting_lines()
    {
        var (buf, _) = Insert("", 0, "a\r\nb\r\nc\r\n");

        Assert.Contains("· 3줄]", buf, StringComparison.Ordinal);
    }

    [Fact]
    public void Expand_restores_the_original_text()
    {
        const string original = "[12:01] ERROR reset\n[12:02] WARN retry\n[12:03] ERROR reset";
        var (buf, _) = Insert("분석: ", 4, original);

        var expanded = PasteStore.Expand(buf);

        Assert.Equal("분석: " + original, expanded);
    }

    [Fact]
    public void Expand_is_a_noop_for_text_without_placeholders()
    {
        Assert.Equal("그냥 텍스트", PasteStore.Expand("그냥 텍스트"));
    }

    [Fact]
    public void Expand_leaves_unknown_placeholders_untouched()
    {
        // 다른 세션에서 온 표식 등 — 조용히 삼키지 않고 그대로 둔다.
        const string s = "앞 [붙여넣기 #99999 · 7줄] 뒤";
        Assert.Equal(s, PasteStore.Expand(s));
    }

    [Fact]
    public void Two_pastes_get_distinct_ids_and_both_expand()
    {
        var sb = new StringBuilder();
        var pos = 0;
        PasteStore.Insert(sb, ref pos, "a1\na2");
        sb.Insert(pos, " / ");
        pos += 3;
        PasteStore.Insert(sb, ref pos, "b1\nb2");

        var expanded = PasteStore.Expand(sb.ToString());
        Assert.Equal("a1\na2 / b1\nb2", expanded);
    }

    [Fact]
    public void Backspace_deletes_a_placeholder_whole()
    {
        var sb = new StringBuilder("앞");
        var pos = 1;
        PasteStore.Insert(sb, ref pos, "x\ny");

        var len = PasteStore.PlaceholderLengthEndingAt(sb.ToString(), pos);

        Assert.True(len > 0);
        sb.Remove(pos - len, len);
        Assert.Equal("앞", sb.ToString());
    }

    [Fact]
    public void PlaceholderLengthEndingAt_returns_zero_for_ordinary_text()
    {
        Assert.Equal(0, PasteStore.PlaceholderLengthEndingAt("hello]", 6));
        Assert.Equal(0, PasteStore.PlaceholderLengthEndingAt("hello", 5));
        Assert.Equal(0, PasteStore.PlaceholderLengthEndingAt("[not a token]", 13));
        Assert.Equal(0, PasteStore.PlaceholderLengthEndingAt("", 0));
    }
}
