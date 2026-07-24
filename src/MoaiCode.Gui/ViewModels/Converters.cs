using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MoaiCode.Gui.ViewModels;

public static class Converters
{
    // 진행 배지 점: 진행중=악센트, 완료=녹색.
    public static readonly IValueConverter DoneToBrush = new FuncValueConverter<bool, IBrush>(
        done => new SolidColorBrush(Color.Parse(done ? "#3FB68B" : "#6D5EF0")));

    // 아이콘 키("Icon.FileText" 등) → App 리소스의 lucide Geometry. 이모지 대신 벡터 아이콘 바인딩용.
    public static readonly IValueConverter IconKey = new FuncValueConverter<string?, Geometry?>(key => Resolve(key));

    // Office 앱(PowerPoint/Excel/Word) → 브랜드 색(PPT 빨강, Excel 녹색, Word 파랑).
    public static readonly IValueConverter AppBrush = new FuncValueConverter<string?, IBrush>(app =>
        new SolidColorBrush(Color.Parse(app switch
        {
            "PowerPoint" => "#D24726",
            "Excel" => "#217346",
            "Word" => "#2B579A",
            _ => "#64748B",
        })));

    // Office 앱 → 대응 문서 아이콘 geometry.
    public static readonly IValueConverter AppIcon = new FuncValueConverter<string?, Geometry?>(app => Resolve(app switch
    {
        "PowerPoint" => "Icon.Presentation",
        "Excel" => "Icon.FileSpreadsheet",
        "Word" => "Icon.FileText",
        _ => "Icon.FileText",
    }));

    // 입력창 윤곽선: 편집 연결 중이면 앱 색, 아니면 테마 기본 테두리.
    public static readonly IValueConverter InputOutline = new FuncValueConverter<string?, IBrush?>(app =>
    {
        var hex = app switch
        {
            "PowerPoint" => "#D24726",
            "Excel" => "#217346",
            "Word" => "#2B579A",
            _ => null,
        };
        if (hex is not null)
        {
            return new SolidColorBrush(Color.Parse(hex));
        }

        return Application.Current?.Resources.TryGetResource(
            "BorderSoftBrush", Application.Current.ActualThemeVariant, out var b) == true
            ? b as IBrush
            : null;
    });

    private static Geometry? Resolve(string? key)
    {
        if (string.IsNullOrEmpty(key) || Application.Current is null)
        {
            return null;
        }

        return Application.Current.Resources.TryGetResource(key, Application.Current.ActualThemeVariant, out var g)
            ? g as Geometry
            : null;
    }
}
