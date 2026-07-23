using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using MoaiCode.Gui.ViewModels;

namespace MoaiCode.Gui.Views;

public partial class SettingsWindow : Window
{
    /// <summary>모델 저장 또는 재로그인으로 설정이 바뀌었는지(호출자가 엔진 재구성 판단).</summary>
    public bool Changed { get; private set; }

    public SettingsWindow()
    {
        AvaloniaXamlLoader.Load(this);
        var vm = new SettingsViewModel();
        DataContext = vm;
        vm.Saved += () =>
        {
            Changed = true;
            Close();
        };
    }

    private async void OnReLogin(object? sender, RoutedEventArgs e)
    {
        var loginVm = new LoginViewModel();
        var login = new LoginWindow { DataContext = loginVm };
        var ok = false;
        loginVm.LoggedIn += () =>
        {
            ok = true;
            login.Close();
        };
        await login.ShowDialog(this);
        if (ok)
        {
            Changed = true;
            Close();
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
