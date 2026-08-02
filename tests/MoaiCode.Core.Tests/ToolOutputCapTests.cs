using MoaiCode.Core.Agent;
using Xunit;

namespace MoaiCode.Core.Tests;

// 거대한 툴 출력이 컨텍스트로 통째로 들어가 컴팩션을 폭주시키던 회귀를 막는다.
public class ToolOutputCapTests
{
    [Fact]
    public void Under_limit_is_returned_unchanged()
    {
        var text = new string('a', 500);
        Assert.Equal(text, QueryEngine.CapToolOutput(text, 1000));
    }

    [Fact]
    public void Zero_or_negative_limit_means_unlimited()
    {
        var text = new string('a', 100_000);
        Assert.Equal(text, QueryEngine.CapToolOutput(text, 0));
        Assert.Equal(text, QueryEngine.CapToolOutput(text, -1));
    }

    [Fact]
    public void Over_limit_keeps_head_and_tail_and_marks_omission()
    {
        var text = new string('H', 8_000) + new string('T', 8_000); // 16,000 chars
        var capped = QueryEngine.CapToolOutput(text, 4_000);

        Assert.True(capped.Length < text.Length);
        Assert.Contains("truncated", capped);          // 생략 표식 존재
        Assert.StartsWith("HHHH", capped);             // 앞부분 보존
        Assert.EndsWith("TTTT", capped);               // 꼬리 보존(오류가 끝에 있을 수 있으므로)
        Assert.Contains("of 16000 chars omitted", capped);
    }
}
