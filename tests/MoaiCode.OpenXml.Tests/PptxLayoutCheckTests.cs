using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using MoaiCode.Core.Tools;
using MoaiCode.Tools.OpenXml;
using Xunit;

namespace MoaiCode.OpenXml.Tests;

// 생성물 기하 QA(PptxLayoutCheck) 회귀 가드. 우리 템플릿 레이아웃은 겹침/화면밖이 없어야 하고,
// 의도적으로 슬라이드 밖에 둔 도형은 검출되어야 한다. (16:9: 12192000 x 6858000 EMU)
public sealed class PptxLayoutCheckTests : IDisposable
{
    private const long SlideW = 12192000;
    private const long SlideH = 6858000;

    private readonly string _dir;
    public PptxLayoutCheckTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "moai-pptxqa-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private async Task<string> Create(object input, string name)
    {
        var ctx = new ToolContext(_dir, PermissionMode.Auto);
        var json = JsonSerializer.SerializeToElement(input);
        await foreach (var p in new PptxCreateTool().ExecuteAsync(json, ctx, CancellationToken.None))
        {
            if (p is ToolOutput o)
            {
                Assert.False(o.IsError, o.Text);
            }
        }

        return Path.Combine(_dir, name);
    }

    private static List<PptxLayoutCheck.Issue> InspectAll(string path)
    {
        using var doc = PresentationDocument.Open(path, false);
        var all = new List<PptxLayoutCheck.Issue>();
        var n = 0;
        foreach (var sp in doc.PresentationPart!.SlideParts)
        {
            all.AddRange(PptxLayoutCheck.Inspect(sp.Slide, ++n, SlideW, SlideH));
        }

        return all;
    }

    [Fact]
    public async Task Clean_deck_all_layouts_has_no_issues()
    {
        var path = await Create(new
        {
            path = "clean.pptx",
            template = "A",
            slides = new object[]
            {
                new { layout = "cover", title = "제목", subtitle = "부제" },
                new { layout = "section", title = "구간", subtitle = "설명" },
                new { layout = "content", title = "내용", subtitle = "부제", bullets = new[] { "첫째 항목", "둘째 항목", "셋째 항목", "넷째 항목" } },
                new { layout = "two_col", title = "비교", columns = new object[]
                {
                    new { heading = "왼쪽", bullets = new[] { "가", "나", "다" } },
                    new { heading = "오른쪽", bullets = new[] { "라", "마", "바" } },
                } },
                new { layout = "stat", title = "지표", metrics = new object[]
                {
                    new { value = "73%", label = "도입" },
                    new { value = "5x", label = "확대" },
                    new { value = "2025", label = "제정" },
                } },
                new { layout = "cards", title = "요점", cards = new object[]
                {
                    new { heading = "A", body = "설명 A" },
                    new { heading = "B", body = "설명 B" },
                    new { heading = "C", body = "설명 C" },
                    new { heading = "D", body = "설명 D" },
                } },
                new { layout = "process", title = "절차", steps = new object[]
                {
                    new { label = "1단계", caption = "설명" },
                    new { label = "2단계", caption = "설명" },
                    new { label = "3단계", caption = "설명" },
                    new { label = "4단계", caption = "설명" },
                } },
                new { layout = "table", title = "표", table = new
                {
                    headers = new[] { "A", "B", "C" },
                    rows = new object[] { new[] { "1", "2", "3" }, new[] { "4", "5", "6" } },
                } },
                new { layout = "quote", title = "한 문장의 강한 메시지" },
            },
        }, "clean.pptx");

        var issues = InspectAll(path);
        Assert.True(issues.Count == 0, "expected no layout issues but found: " + string.Join(" | ", issues.Select(i => $"s{i.Slide}:{i.Kind} {i.Detail}")));
    }

    [Fact]
    public async Task Off_slide_shape_is_detected()
    {
        // 자유 도형을 슬라이드(13.33") 밖 x=20" 에 배치 → off-slide 로 검출되어야 한다.
        var path = await Create(new
        {
            path = "bad.pptx",
            template = "A",
            slides = new object[]
            {
                new { layout = "content", title = "제목", bullets = new[] { "항목" },
                      shapes = new object[] { new { x = 20.0, y = 1.0, w = 3.0, h = 1.0, text = "밖" } } },
            },
        }, "bad.pptx");

        var issues = InspectAll(path);
        Assert.Contains(issues, i => i.Kind == "offslide");
    }

    [Theory]
    [InlineData("94%", 44)]                 // 짧은 값은 44pt 유지
    [InlineData("90분 → 5분", 36)]          // 4장 카드(폭≈2.4M EMU)에서 화살표 값은 축소
    [InlineData("1,234,567,890원", 24)]      // 매우 길면 하한
    public void FitFontPt_ShrinksLongStatValues(string value, int expectedMax)
    {
        // 4장 카드 안쪽 폭: (ContentW - 3*gap)/4 - 2*180000
        long cardW = (PptxDesign.ContentW - 360000 * 3) / 4 - 360000;
        var pt = PptxDesign.FitFontPt(value, cardW, 44, 24);
        Assert.True(pt <= expectedMax, $"{value}: {pt}pt");
        Assert.InRange(pt, 24, 44);
    }
}
