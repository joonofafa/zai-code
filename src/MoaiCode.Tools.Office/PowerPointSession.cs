using System.Runtime.Versioning;
using MoaiCode.Config;

namespace MoaiCode.Tools.Office;

/// <summary>
/// 실행 중인 PowerPoint 에 late-binding(dynamic) 으로 연결해 활성 프레젠테이션을 조회한다.
/// late-binding 이라 Office PIA 참조 없이도 컴파일된다(dynamic 은 런타임 바인딩).
/// 모든 COM 접근은 StaDispatcher 안에서 수행하고, 밖으로는 DTO 만 반환한다.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PowerPointSession
{
    private readonly StaDispatcher _sta;

    public PowerPointSession(StaDispatcher sta) => _sta = sta;

    /// <summary>활성 프레젠테이션 스냅샷. PowerPoint 미실행/문서 없음이면 null.</summary>
    public Task<PresentationInfo?> GetActivePresentationAsync(
        int maxSlides = 100, int maxShapesPerSlide = 200) =>
        _sta.InvokeAsync(() => Inspect(maxSlides, maxShapesPerSlide));

    private static PresentationInfo? Inspect(int maxSlides, int maxShapesPerSlide)
    {
        MoaiLog.Debug($"PowerPointInspect: enter on thread apartment={System.Threading.Thread.CurrentThread.GetApartmentState()}");

        dynamic? app = ComInterop.TryGetActiveObject("PowerPoint.Application");
        if (app is null)
        {
            MoaiLog.Warn("PowerPointInspect: no running PowerPoint instance (app is null)");
            return null; // PowerPoint 미실행
        }

        dynamic? pres = TryGet(() => app.ActivePresentation, "app.ActivePresentation");
        if (pres is null)
        {
            MoaiLog.Warn("PowerPointInspect: PowerPoint running but ActivePresentation is null (no open document?)");
            return null; // 열린 프레젠테이션 없음
        }

        int? current = null;
        var win = TryGet(() => app.ActiveWindow);
        if (win is not null)
        {
            current = TryGet(() => (int?)win.View.Slide.SlideIndex);
        }

        var slides = new List<SlideInfo>();
        int slideCount = (int)pres.Slides.Count;
        for (var i = 1; i <= slideCount && slides.Count < maxSlides; i++)
        {
            dynamic slide = pres.Slides[i];
            var shapes = new List<ShapeInfo>();

            // 본문 도형 + 레이아웃/마스터 배경 도형(전체 테마 색 변경 등에서 대상이 되도록).
            AddShapes(shapes, TryGet(() => slide.Shapes), "slide", maxShapesPerSlide);
            AddShapes(shapes, TryGet(() => slide.CustomLayout.Shapes), "layout", maxShapesPerSlide);
            AddShapes(shapes, TryGet(() => slide.CustomLayout.SlideMaster.Shapes), "master", maxShapesPerSlide);

            slides.Add(new SlideInfo(
                Index: (int)slide.SlideIndex,
                SlideId: (int)slide.SlideID,
                LayoutName: TryGet(() => (string?)slide.CustomLayout.Name),
                Shapes: shapes));
        }

        return new PresentationInfo(
            Name: (string)pres.Name,
            Path: TryGet(() => (string?)pres.FullName),
            SlideCount: slideCount,
            CurrentSlideIndex: current,
            Slides: slides);
    }

    // 한 Shapes 컬렉션(본문/레이아웃/마스터)을 ShapeInfo 로 변환해 목록에 추가한다.
    private static void AddShapes(List<ShapeInfo> shapes, dynamic? comShapes, string scope, int max)
    {
        if (comShapes is null)
        {
            return;
        }

        int count = TryGet(() => (int?)comShapes.Count) ?? 0;
        var added = 0;
        for (var j = 1; j <= count && added < max; j++)
        {
            dynamic? shape = TryGet(() => comShapes[j]);
            if (shape is null)
            {
                continue;
            }

            bool hasTextFrame = TriBool(shape.HasTextFrame);
            bool hasText = hasTextFrame && TriBool(shape.TextFrame.HasText);
            string? text = hasText ? (string?)shape.TextFrame.TextRange.Text : null;

            // 텍스트 도형이면 대표 서식(크기·글꼴·굵기·색) — 모델이 슬라이드 톤을 보고 판단하도록.
            double? fontSize = null;
            string? fontName = null;
            bool? bold = null;
            string? fontColor = null;
            if (hasText)
            {
                dynamic font = shape.TextFrame.TextRange.Font;
                fontSize = TryGet(() => (double?)Convert.ToDouble(font.Size));
                fontName = TryGet(() => (string?)font.Name);
                var b = TryGet(() => (int?)font.Bold); // MsoTriState
                bold = b is -1 ? true : b is 0 ? false : null;
                fontColor = ColorHex(TryGet(() => (int?)font.Color.RGB));
            }

            shapes.Add(new ShapeInfo(
                ShapeId: (int)shape.Id,
                Name: (string)shape.Name,
                Text: text,
                Left: Convert.ToDouble(shape.Left),
                Top: Convert.ToDouble(shape.Top),
                Width: Convert.ToDouble(shape.Width),
                Height: Convert.ToDouble(shape.Height),
                HasTextFrame: hasTextFrame,
                FontSize: fontSize,
                FontName: fontName,
                Bold: bold,
                FontColor: fontColor,
                Scope: scope));
            added++;
        }
    }

    // Office COM 색(BGR int) → "#RRGGBB". 음수/특수값은 null.
    private static string? ColorHex(int? bgr)
    {
        if (bgr is null || bgr.Value < 0)
        {
            return null;
        }

        var c = bgr.Value;
        return $"#{c & 0xFF:X2}{(c >> 8) & 0xFF:X2}{(c >> 16) & 0xFF:X2}";
    }

    // Office COM 의 불리언 속성은 bool 이 아니라 MsoTriState(int: msoTrue=-1, msoFalse=0)로 온다.
    // dynamic 에서 (bool) 로 직접 캐스팅하면 RuntimeBinderException('int'->'bool') 이 나므로 int 로 변환한다.
    private static bool TriBool(dynamic triState) => (int)triState != 0;

    // COM 속성 접근이 예외(문서 없음/보호된 보기 등)를 던지면 null 로 흡수하되, 진단 로그를 남긴다.
    private static T? TryGet<T>(Func<T> get, string? what = null)
    {
        try
        {
            return get();
        }
        catch (Exception ex)
        {
            if (what is not null)
            {
                MoaiLog.Debug($"PowerPointInspect: COM access '{what}' failed: {ex.GetType().Name}: {ex.Message}");
            }

            return default;
        }
    }
}
