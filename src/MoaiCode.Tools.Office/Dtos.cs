namespace MoaiCode.Tools.Office;

// COM 객체 대신 STA 스레드 밖으로 넘기는 안정적 스냅샷(DTO). 툴 경계에는 이것만 노출한다.
// 원본 설계: docu_work_interaction.md §상태 식별과 동시 편집.

/// <summary>활성 프레젠테이션 스냅샷.</summary>
public sealed record PresentationInfo(
    string Name,
    string? Path,
    int SlideCount,
    int? CurrentSlideIndex,
    IReadOnlyList<SlideInfo> Slides);

/// <summary>슬라이드 스냅샷. SlideId 는 인덱스와 달리 안정적 식별자(순서 변경에도 유지).</summary>
public sealed record SlideInfo(
    int Index,
    int SlideId,
    string? LayoutName,
    IReadOnlyList<ShapeInfo> Shapes);

/// <summary>도형 스냅샷. ShapeId 우선 식별, Name 은 보조(중복 가능).</summary>
public sealed record ShapeInfo(
    int ShapeId,
    string Name,
    string? Text,
    double Left,
    double Top,
    double Width,
    double Height,
    bool HasTextFrame,
    // 서식(모델이 슬라이드 톤을 보고 편집을 판단하도록). 텍스트 없으면 null.
    double? FontSize = null,
    string? FontName = null,
    bool? Bold = null,
    string? FontColor = null);
