using System.Text;
using MoaiCode.Tools.Web;
using Xunit;

namespace MoaiCode.Core.Tests;

/// <summary>
/// WebFetch 의 문자셋 판별 회귀 테스트. 예전엔 Encoding.UTF8.GetString 하드코딩이라
/// EUC-KR/CP949 한국어 페이지가 전부 깨졌다.
/// </summary>
public sealed class WebFetchDecodeTests
{
    // "한글" (EUC-KR). InvariantGlobalization=true 환경에서도 디코딩되어야 한다.
    private static readonly byte[] EucKrHangul = { 0xC7, 0xD1, 0xB1, 0xDB };

    [Fact]
    public void DecodeText_UsesCharsetFromContentType()
    {
        var text = WebFetchTool.DecodeText(EucKrHangul, "euc-kr", "text/html");
        Assert.Equal("한글", text);
    }

    [Fact]
    public void DecodeText_UsesCharsetFromHtmlMetaTag_WhenHeaderMissing()
    {
        var html = Concat(
            Encoding.ASCII.GetBytes("<html><head><meta charset=\"euc-kr\"></head><body>"),
            EucKrHangul);

        var text = WebFetchTool.DecodeText(html, charset: null, media: "text/html");
        Assert.Contains("한글", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodeText_HandlesLegacyMetaHttpEquivForm()
    {
        var html = Concat(
            Encoding.ASCII.GetBytes(
                "<meta http-equiv=\"Content-Type\" content=\"text/html; charset=ks_c_5601-1987\">"),
            EucKrHangul);

        var text = WebFetchTool.DecodeText(html, charset: null, media: "text/html");
        Assert.Contains("한글", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodeText_ContentTypeCharsetWins_OverMetaTag()
    {
        // 헤더가 있으면 meta 를 훑지 않는다.
        var html = Concat(
            Encoding.ASCII.GetBytes("<meta charset=\"utf-8\">"),
            EucKrHangul);

        var text = WebFetchTool.DecodeText(html, "euc-kr", "text/html");
        Assert.Contains("한글", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodeText_StripsUtf8Bom()
    {
        var bytes = Concat(new byte[] { 0xEF, 0xBB, 0xBF }, Encoding.UTF8.GetBytes("hi"));

        var text = WebFetchTool.DecodeText(bytes, charset: null, media: "text/plain");
        Assert.Equal("hi", text);
    }

    [Fact]
    public void DecodeText_BomWins_OverContentTypeCharset()
    {
        var bytes = Concat(new byte[] { 0xEF, 0xBB, 0xBF }, Encoding.UTF8.GetBytes("한글"));

        var text = WebFetchTool.DecodeText(bytes, "euc-kr", "text/html");
        Assert.Equal("한글", text);
    }

    [Fact]
    public void DecodeText_Utf16LeBom()
    {
        var bytes = Concat(new byte[] { 0xFF, 0xFE }, Encoding.Unicode.GetBytes("한글"));

        var text = WebFetchTool.DecodeText(bytes, charset: null, media: "text/plain");
        Assert.Equal("한글", text);
    }

    [Fact]
    public void DecodeText_FallsBackToUtf8_OnUnknownCharset()
    {
        var bytes = Encoding.UTF8.GetBytes("한글");

        var text = WebFetchTool.DecodeText(bytes, "x-nonexistent-charset", "text/html");
        Assert.Equal("한글", text);
    }

    [Fact]
    public void DecodeText_DefaultsToUtf8_WhenNoCharsetAnywhere()
    {
        var bytes = Encoding.UTF8.GetBytes("한글");

        var text = WebFetchTool.DecodeText(bytes, charset: null, media: "application/json");
        Assert.Equal("한글", text);
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        a.CopyTo(r, 0);
        b.CopyTo(r, a.Length);
        return r;
    }
}
