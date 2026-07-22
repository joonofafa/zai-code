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
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 우선순위: MOAI_GUI_THEME(개발/캡처) → 저장된 설정 → (없으면) 최초 선택창.
            var theme = Environment.GetEnvironmentVariable("MOAI_GUI_THEME");
            if (string.IsNullOrWhiteSpace(theme))
            {
                theme = GuiSettings.Load().Theme;
            }

            if (string.IsNullOrWhiteSpace(theme))
            {
                ShowThemeChooser(desktop);
            }
            else
            {
                ApplyTheme(theme);
                desktop.MainWindow = new MainWindow { DataContext = new MainViewModel() };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ShowThemeChooser(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var chooser = new ThemeChooserWindow();
        chooser.ThemeChosen += chosen =>
        {
            var settings = GuiSettings.Load();
            settings.Theme = chosen;
            settings.Save();

            ApplyTheme(chosen);
            var main = new MainWindow { DataContext = new MainViewModel() };
            desktop.MainWindow = main;
            main.Show();
            chooser.Close();
        };
        desktop.MainWindow = chooser;
    }

    private void ApplyTheme(string theme) =>
        RequestedThemeVariant = string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase)
            ? ThemeVariant.Light
            : ThemeVariant.Dark;
}
