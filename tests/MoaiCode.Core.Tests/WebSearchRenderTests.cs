using System.Text.Json;
using MoaiCode.Core.Tools;
using MoaiCode.Tools.Web;
using Xunit;

namespace MoaiCode.Core.Tests;

// WebSearch(Gemini Google Search grounding): 응답에서 답변 텍스트와 출처(url_citation)를 뽑아내는지.
// grounding 응답은 결과 목록이 아니라 steps 안에 답변/주석이 섞여 오므로 파싱이 깨지기 쉽다.
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
    public async Task Reports_missing_key_without_calling_out()
    {
        var prev = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        Environment.SetEnvironmentVariable("GEMINI_API_KEY", null);
        try
        {
            var (text, err) = await RunAsync(new { query = "hello" });
            Assert.True(err);
            Assert.Contains("GEMINI_API_KEY", text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", prev);
        }
    }

    [Fact]
    public void Render_extracts_answer_text_and_deduplicated_sources()
    {
        // 문서에 나온 steps 형태: thought → google_search_call → google_search_result → model_output.
        const string Body = """
        {
          "steps": [
            { "type": "thought", "content": [ { "type": "text", "text": "IGNORED_THOUGHT" } ] },
            { "type": "google_search_call", "queries": ["euro 2024 winner"] },
            { "type": "google_search_result", "search_suggestions": "<div>widget</div>" },
            {
              "type": "model_output",
              "content": [
                {
                  "type": "text",
                  "text": "Spain won Euro 2024.",
                  "annotations": [
                    { "type": "url_citation", "url": "https://uefa.com/a", "title": "UEFA" },
                    { "type": "url_citation", "url": "https://uefa.com/a", "title": "UEFA dup" },
                    { "type": "url_citation", "url": "https://bbc.com/b", "title": "BBC" }
                  ]
                }
              ]
            }
          ]
        }
        """;

        var render = typeof(WebSearchTool).GetMethod(
            "Render", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var outText = (string)render.Invoke(null, new object[] { Body })!;

        Assert.Contains("Spain won Euro 2024.", outText);
        Assert.Contains("https://uefa.com/a", outText);
        Assert.Contains("https://bbc.com/b", outText);
        // 같은 URL 이 두 번 인용돼도 출처 목록엔 한 번만.
        Assert.Equal(1, outText.Split("https://uefa.com/a").Length - 1);
        // 렌더는 답변 뒤에 출처를 붙인다.
        Assert.Contains("Sources:", outText);
    }

    [Fact]
    public void Render_survives_unexpected_shape()
    {
        var render = typeof(WebSearchTool).GetMethod(
            "Render", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert.Equal("", (string)render.Invoke(null, new object[] { "not json at all" })!);
        Assert.Equal("", (string)render.Invoke(null, new object[] { """{"steps":[]}""" })!);
    }
}
