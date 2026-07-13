using System.Runtime.Versioning;

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
        dynamic? app = ComInterop.TryGetActiveObject("PowerPoint.Application");
        if (app is null)
        {
            return null; // PowerPoint 미실행
        }

        dynamic? pres = TryGet(() => app.ActivePresentation);
        if (pres is null)
        {
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
            int shapeCount = (int)slide.Shapes.Count;
            for (var j = 1; j <= shapeCount && shapes.Count < maxShapesPerSlide; j++)
            {
                dynamic shape = slide.Shapes[j];
                bool hasText = (bool)shape.HasTextFrame && (bool)shape.TextFrame.HasText;
                string? text = hasText ? (string?)shape.TextFrame.TextRange.Text : null;
                shapes.Add(new ShapeInfo(
                    ShapeId: (int)shape.Id,
                    Name: (string)shape.Name,
                    Text: text,
                    Left: (double)shape.Left,
                    Top: (double)shape.Top,
                    Width: (double)shape.Width,
                    Height: (double)shape.Height,
                    HasTextFrame: (bool)shape.HasTextFrame));
            }

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

    // COM 속성 접근이 예외(문서 없음/보호된 보기 등)를 던지면 null 로 흡수.
    private static T? TryGet<T>(Func<T> get)
    {
        try
        {
            return get();
        }
        catch
        {
            return default;
        }
    }
}
