using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MoaiCode.Gui.ViewModels;

public static class Converters
{
    // 진행 배지 점: 진행중=악센트, 완료=녹색.
    public static readonly IValueConverter DoneToBrush = new FuncValueConverter<bool, IBrush>(
        done => new SolidColorBrush(Color.Parse(done ? "#3FB68B" : "#6D5EF0")));
}
