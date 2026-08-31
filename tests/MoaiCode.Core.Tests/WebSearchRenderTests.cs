using System.Text.Json;
using MoaiCode.Core.Tools;
using MoaiCode.Tools.Web;
using Xunit;

namespace MoaiCode.Core.Tests;

// WebSearch(z.ai 내장 web_search): 응답 최상위 web_search 배열을 결과 목록으로 렌더한다.
// 스키마가 바뀌거나 필드가 비어도 죽지 않아야 한다(검색이 세션을 막으면 안 된다).
public sealed class WebSearchRenderTests
{
    private static async Task<(string Text, bool Error)> RunAsync(object input)
    {
        var json = JsonSerializer.SerializeToElement(input);
        var ctx = new ToolContext(Path.GetTempPath(), PermissionMode.Auto);
        var text = "";
        var err = false;
        await foreach (var p in new WebSearchTool().ExecuteAsync(json, ctx, CancellationToken.None))
        {
            if (p is ToolOutput o) { text += o.Text; err |= o.IsError; }
        }

        return (text, err);
    }

    [Fact]
    public async Task Requires_query()
    {
        var (_, err) = await RunAsync(new { query = "" });
        Assert.True(err);
    }

    [Fact]
    public void Renders_title_url_date_and_snippet()
    {
        const string Body = """
        {
          "choices": [{"message": {"content": ""}}],
          "web_search": [
            {"title": "Option.Arity Property", "link": "https://learn.microsoft.com/x",
             "content": "Gets or sets the arity.", "publish_date": "2026-01-02"},
            {"title": "두 번째", "link": "https://example.com/2", "content": "본문"}
          ]
        }
        """;
        var outText = WebSearchTool.Render(Body);

        Assert.Contains("1. Option.Arity Property", outText);
        Assert.Contains("https://learn.microsoft.com/x", outText);
        Assert.Contains("[2026-01-02] Gets or sets the arity.", outText);
        Assert.Contains("2. 두 번째", outText);
    }

    [Fact]
    public void Skips_entries_without_title_or_link()
    {
        const string Body = """
        {"web_search": [{"content": "제목도 링크도 없음"}, {"title": "쓸모 있음", "link": "https://a/b"}]}
        """;
        var outText = WebSearchTool.Render(Body);
        Assert.DoesNotContain("제목도 링크도 없음", outText);
        Assert.Contains("1. 쓸모 있음", outText);   // 번호는 실제 출력된 것만 센다
    }

    [Theory]
    // 검색 결과가 없거나 응답 형태가 예상과 다르면 빈 문자열 → 호출부가 "(no results)" 로 처리한다.
    [InlineData("""{"web_search": []}""")]
    [InlineData("""{"choices": [{"message": {"content": "답변만 있고 검색 결과 없음"}}]}""")]
    [InlineData("""{"web_search": "배열이 아님"}""")]
    [InlineData("not json at all")]
    public void Returns_empty_for_missing_or_odd_shapes(string body)
        => Assert.Equal("", WebSearchTool.Render(body));
}
