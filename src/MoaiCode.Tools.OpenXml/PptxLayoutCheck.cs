using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Presentation;
using D = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace MoaiCode.Tools.OpenXml;

/// <summary>
/// 생성된 슬라이드의 기하 QA — grok/Anthropic pptx 스킬의 check_overlaps 접근을 이식.
/// 텍스트 도형끼리의 겹침(작은 도형 면적의 임계% 초과)과 슬라이드 경계 이탈(off-slide)을 검출한다.
/// 배경/장식(빈 텍스트: 패널·강조바)은 겹침 대상에서 제외한다. 순수 함수(부수효과 없음).
/// </summary>
public static class PptxLayoutCheck
{
    public sealed record Issue(int Slide, string Kind, string Detail); // Kind: "overlap" | "offslide"

    // 경계·면적 판정의 잡음 완화용 허용 오차(EMU ≈ 0.01").
    private const long Tol = 9525;

    private readonly record struct Box(string Name, long X, long Y, long W, long H, bool HasText)
    {
        public long Right => X + W;
        public long Bottom => Y + H;
        public long Area => W * H;
    }

    /// <summary>슬라이드 하나를 검사해 이슈 목록을 돌려준다(빈 목록 = 정상).</summary>
    public static List<Issue> Inspect(Slide slide, int slideNo, long slideW, long slideH, double thresholdPct = 10.0)
    {
        var issues = new List<Issue>();
        var tree = slide.CommonSlideData?.ShapeTree;
        if (tree is null)
        {
            return issues;
        }

        var boxes = new List<Box>();
        foreach (var el in tree.ChildElements)
        {
            var box = ToBox(el);
            if (box is { } b && b.W > Tol && b.H > Tol)
            {
                boxes.Add(b);
            }
        }

        // 1) off-slide: 어떤 도형이든 슬라이드 경계를 (허용오차 넘어) 벗어나면 보고.
        foreach (var b in boxes)
        {
            if (b.X < -Tol || b.Y < -Tol || b.Right > slideW + Tol || b.Bottom > slideH + Tol)
            {
                issues.Add(new Issue(slideNo, "offslide",
                    $"{b.Name} at ({Inch(b.X)},{Inch(b.Y)}) size ({Inch(b.W)}x{Inch(b.H)}) exceeds slide"));
            }
        }

        // 2) 텍스트-텍스트 겹침: 교집합 면적이 더 작은 도형 면적의 임계% 를 넘으면 보고.
        var texts = boxes.Where(b => b.HasText).ToList();
        for (var i = 0; i < texts.Count; i++)
        {
            for (var j = i + 1; j < texts.Count; j++)
            {
                var area = OverlapArea(texts[i], texts[j]);
                if (area <= 0)
                {
                    continue;
                }

                var smaller = Math.Min(texts[i].Area, texts[j].Area);
                if (smaller > 0 && (double)area / smaller * 100.0 >= thresholdPct)
                {
                    issues.Add(new Issue(slideNo, "overlap",
                        $"{texts[i].Name} overlaps {texts[j].Name} ({(double)area / smaller * 100.0:F0}% of smaller)"));
                }
            }
        }

        return issues;
    }

    private static Box? ToBox(OpenXmlElement el) => el switch
    {
        P.Shape sp => Make(
            Name(sp.NonVisualShapeProperties?.NonVisualDrawingProperties),
            sp.ShapeProperties?.Transform2D, HasText(sp)),
        P.GraphicFrame gf => Make(
            Name(gf.NonVisualGraphicFrameProperties?.NonVisualDrawingProperties),
            gf.Transform, HasText(gf)),
        P.Picture pic => Make(
            Name(pic.NonVisualPictureProperties?.NonVisualDrawingProperties),
            pic.ShapeProperties?.Transform2D, false),
        _ => null,
    };

    private static Box? Make(string name, D.Transform2D? xfrm, bool hasText)
    {
        var off = xfrm?.Offset;
        var ext = xfrm?.Extents;
        if (off?.X is null || off.Y is null || ext?.Cx is null || ext.Cy is null)
        {
            return null;
        }

        return new Box(name, off.X!, off.Y!, ext.Cx!, ext.Cy!, hasText);
    }

    // GraphicFrame 은 P.Transform(Offset/Extents) 을 쓴다.
    private static Box? Make(string name, P.Transform? xfrm, bool hasText)
    {
        var off = xfrm?.Offset;
        var ext = xfrm?.Extents;
        if (off?.X is null || off.Y is null || ext?.Cx is null || ext.Cy is null)
        {
            return null;
        }

        return new Box(name, off.X!, off.Y!, ext.Cx!, ext.Cy!, hasText);
    }

    private static string Name(NonVisualDrawingProperties? nv) =>
        nv?.Name?.Value is { Length: > 0 } n ? n : "(unnamed)";

    private static bool HasText(OpenXmlElement el) =>
        el.Descendants<D.Text>().Any(t => !string.IsNullOrWhiteSpace(t.Text));

    private static long OverlapArea(in Box a, in Box b)
    {
        var ix1 = Math.Max(a.X, b.X);
        var iy1 = Math.Max(a.Y, b.Y);
        var ix2 = Math.Min(a.Right, b.Right);
        var iy2 = Math.Min(a.Bottom, b.Bottom);
        if (ix1 >= ix2 || iy1 >= iy2)
        {
            return 0;
        }

        return (ix2 - ix1) * (iy2 - iy1);
    }

    private static string Inch(long emu) => (emu / 914400.0).ToString("F2");
}
