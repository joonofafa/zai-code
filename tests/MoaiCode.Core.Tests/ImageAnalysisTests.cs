using System.Text.Json;
using MoaiCode.Core.Tools;
using MoaiCode.Tools.Media;
using StbImageSharp;
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

    // 시험용 PNG: 채널마다 좌표로 정해지는 무늬(Raster.EncodePng — 예전엔 ImageSharp 로 만들었다).
    private static byte[] Png(int w, int h, int channels = 4, Func<int, int, int, byte>? px = null)
    {
        px ??= (x, y, c) => c == 3 ? (byte)255 : (byte)((x * 7 + y * 13 + c * 50) & 0xFF);
        var data = new byte[w * h * channels];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                for (var c = 0; c < channels; c++)
                {
                    data[(y * w + x) * channels + c] = px(x, y, c);
                }
            }
        }

        return Raster.EncodePng(data, w, h, channels);
    }

    private static ImageResult Decode(byte[] png) => ImageResult.FromMemory(png, ColorComponents.RedGreenBlueAlpha);

    [Fact]
    public void EnsureMinEdge_upscales_small_image()
    {
        var (bytes, media) = ImageAnalysisTool.EnsureMinEdge(Png(100, 40), "image/png");

        var outImg = Decode(bytes);
        Assert.Equal(300, outImg.Width);   // 3배(최대 배율) 확대
        Assert.Equal(120, outImg.Height);  // 40 * 3
        Assert.Equal("image/png", media);
    }

    [Fact]
    public void EnsureMinEdge_keeps_large_image_untouched()
    {
        var original = Png(1024, 768, channels: 3);

        var (bytes, media) = ImageAnalysisTool.EnsureMinEdge(original, "image/png");
        Assert.Same(original, bytes);   // 재인코딩 없이 원본 그대로
        Assert.Equal("image/png", media);
    }

    [Fact]
    public void EnsureMinEdge_falls_back_to_original_on_non_image()
    {
        var notImage = new byte[] { 1, 2, 3, 4, 5 };
        var (bytes, media) = ImageAnalysisTool.EnsureMinEdge(notImage, "image/png");
        Assert.Same(notImage, bytes);
        Assert.Equal("image/png", media);
    }

    [Fact]
    public void EnsureMinEdge_caps_at_3x()
    {
        // 10px 짧은 변은 51.2배가 필요하지만 3배 제한 → 30px.
        var (bytes, _) = ImageAnalysisTool.EnsureMinEdge(Png(10, 20), "image/png");

        var outImg = Decode(bytes);
        Assert.Equal(30, outImg.Width);
        Assert.Equal(60, outImg.Height);
    }

    // 원본에 알파가 없으면 확대본도 RGB 로 저장한다(크기 절약). 알파가 있으면 유지.
    [Fact]
    public void EnsureMinEdge_keeps_alpha_only_when_source_has_it()
    {
        var rgb = Decode(ImageAnalysisTool.EnsureMinEdge(Png(50, 50, channels: 3), "image/png").Bytes);
        Assert.Equal(ColorComponents.RedGreenBlue, rgb.SourceComp);

        var rgba = Decode(ImageAnalysisTool.EnsureMinEdge(Png(50, 50, channels: 4), "image/png").Bytes);
        Assert.Equal(ColorComponents.RedGreenBlueAlpha, rgba.SourceComp);
    }

    // 헤더상 치수가 상한을 넘으면(decompression bomb) 디코드 전에 거부한다.
    [Fact]
    public void EnsureMinEdge_rejects_oversized_dimensions_before_decoding()
    {
        var huge = Png(12_001, 2, channels: 3, px: (_, _, _) => 0);
        Assert.Throws<InvalidDataException>(() => ImageAnalysisTool.EnsureMinEdge(huge, "image/png"));
    }

    // 디코드할 수 없는 형식(WebP 등)과 GIF(애니메이션 보존)는 원본 그대로 보낸다.
    [Fact]
    public void EnsureMinEdge_passes_through_gif_and_undecodable_formats()
    {
        // 1x1 GIF89a
        var gif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
        Assert.Same(gif, ImageAnalysisTool.EnsureMinEdge(gif, "image/gif").Bytes);

        var webpLike = "RIFF\0\0\0\0WEBPVP8 "u8.ToArray();
        Assert.Same(webpLike, ImageAnalysisTool.EnsureMinEdge(webpLike, "image/webp").Bytes);
    }

    // Raster.EncodePng 는 무손실이다 — 디코드하면 같은 픽셀이 나온다(RGB·RGBA 모두).
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void EncodePng_round_trips_pixels(int channels)
    {
        const int w = 37, h = 23;
        var data = new byte[w * h * channels];
        new Random(7).NextBytes(data);
        var png = Raster.EncodePng(data, w, h, channels);

        var back = ImageResult.FromMemory(png, channels == 4 ? ColorComponents.RedGreenBlueAlpha : ColorComponents.RedGreenBlue);
        Assert.Equal((w, h), (back.Width, back.Height));
        Assert.Equal(data, back.Data);
    }

    // 단색은 확대해도 단색(가중치 정규화), 경계 밖 참조는 가장자리로 클램프.
    [Fact]
    public void Resize_keeps_a_flat_color_flat()
    {
        var data = new byte[8 * 6 * 4];
        for (var i = 0; i < data.Length; i += 4)
        {
            (data[i], data[i + 1], data[i + 2], data[i + 3]) = ((byte)200, (byte)30, (byte)90, (byte)255);
        }

        var up = Raster.Resize(data, 8, 6, 4, 24, 18);
        Assert.Equal(24 * 18 * 4, up.Length);
        for (var i = 0; i < up.Length; i += 4)
        {
            Assert.Equal((200, 30, 90, 255), (up[i], up[i + 1], up[i + 2], up[i + 3]));
        }
    }
}
