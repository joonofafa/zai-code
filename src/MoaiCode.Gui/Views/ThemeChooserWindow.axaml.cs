using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace MoaiCode.Gui.Views;

public partial class ThemeChooserWindow : Window
{
    /// <summary>선택된 테마("light"/"dark")를 전달.</summary>
    public event Action<string>? ThemeChosen;

    public ThemeChooserWindow() => AvaloniaXamlLoader.Load(this);

    private void OnLight(object? sender, RoutedEventArgs e) => ThemeChosen?.Invoke("light");

    private void OnDark(object? sender, RoutedEventArgs e) => ThemeChosen?.Invoke("dark");
}
