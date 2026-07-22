using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using MoaiCode.Gui.ViewModels;
using MoaiCode.Gui.Views;

namespace MoaiCode.Gui;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // 테마 선택(기본 다크). MOAI_GUI_THEME=light 로 라이트.
        var theme = Environment.GetEnvironmentVariable("MOAI_GUI_THEME");
        RequestedThemeVariant = string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase)
            ? ThemeVariant.Light
            : ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow { DataContext = new MainViewModel() };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
