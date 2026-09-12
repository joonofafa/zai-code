using System.Text.Json;
using MoaiCode.Core.Tools;
using MoaiCode.Tools.Media;
using Xunit;

// ImageAnalysis(z.ai 비전 모델): 입력 검증·응답 파싱·data URL 조립.
// 실제 API 호출은 포함하지 않는다(오프라인 테스트).
public sealed class ImageAnalysisTests
{
    private static async Task<(string Text, bool Error)> RunAsync(object input)
    {
        var json = JsonSerializer.SerializeToElement(input);
        var ctx = new ToolContext(Path.GetTempPath(), PermissionMode.Auto);
        var text = "";
        var err = false;
        await foreach (var p in new ImageAnalysisTool().ExecuteAsync(json, ctx, CancellationToken.None))
        {
            if (p is ToolOutput o) { text += o.Text; err |= o.IsError; }
        }

        return (text, err);
    }

    [Fact]
    public async Task Requires_image_source()
    {
        var (_, err) = await RunAsync(new { image_source = "", prompt = "what is this?" });
        Assert.True(err);
    }

    [Fact]
    public async Task Requires_prompt()
    {
        var (_, err) = await RunAsync(new { image_source = "/tmp/x.png", prompt = "" });
        Assert.True(err);
    }

    [Fact]
    public async Task Missing_local_file_is_an_error()
    {
        var (text, err) = await RunAsync(new
        {
            image_source = "/tmp/does-not-exist-zai-vision-test.png",
            prompt = "what is this?",
        });
        Assert.True(err);
        Assert.Contains("ImageAnalysis", text);
    }

    [Fact]
    public void RenderAnswer_extracts_content()
    {
        const string Body = """
        {"choices":[{"finish_reason":"stop","message":{"content":"The chart shows a rising trend.","role":"assistant"}}]}
        """;
        Assert.Equal("The chart shows a rising trend.", ImageAnalysisTool.RenderAnswer(Body));
    }

    [Fact]
    public void RenderAnswer_empty_content_returns_hint()
    {
        const string Body = """
        {"choices":[{"finish_reason":"length","message":{"content":"","reasoning_content":"thinking…","role":"assistant"}}]}
        """;
        Assert.Contains("empty answer", ImageAnalysisTool.RenderAnswer(Body));
    }

    [Fact]
    public void RenderAnswer_invalid_json_returns_raw_body()
    {
        Assert.Equal("not json", ImageAnalysisTool.RenderAnswer("not json"));
    }

    [Fact]
    public void RenderAnswer_defensive_on_missing_choices()
    {
        Assert.Equal(string.Empty, ImageAnalysisTool.RenderAnswer("""{"error":{"code":"1"}}"""));
    }

    [Fact]
    public void BuildDataUrl_encodes_mime_and_base64()
    {
        var url = ImageAnalysisTool.BuildDataUrl("image/jpeg", new byte[] { 1, 2, 3 });
        Assert.StartsWith("data:image/jpeg;base64,", url);
        Assert.EndsWith(Convert.ToBase64String(new byte[] { 1, 2, 3 }), url);
    }

    [Fact]
    public void BuildDataUrl_defaults_to_png()
    {
        var url = ImageAnalysisTool.BuildDataUrl("", new byte[] { 0 });
        Assert.StartsWith("data:image/png;base64,", url);
    }
}
