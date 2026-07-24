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
    public static readonly IValueConverter IconKey = new FuncValueConverter<string?, Geometry?>(key =>
    {
        if (string.IsNullOrEmpty(key) || Application.Current is null)
        {
            return null;
        }

        return Application.Current.Resources.TryGetResource(key, Application.Current.ActualThemeVariant, out var g)
            ? g as Geometry
            : null;
    });
}
