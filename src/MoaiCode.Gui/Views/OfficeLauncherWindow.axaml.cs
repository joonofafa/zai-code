using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using MoaiCode.Tools.Office;

namespace MoaiCode.Gui.Views;

/// <summary>
/// "새로운 오피스 문서 작업" 창. 현재 COM 으로 열려 있는 Office 문서를 보여주고,
/// - 목록에서 문서를 선택하면 그 문서로 세션을 연다(PickAsync 결과로 반환 → 메인 VM 이 활성화·채팅 시작).
/// - [새로운 문서 작업] → PowerPoint/Word/Excel 중 선택해 해당 앱을 실행한다(Windows).
/// </summary>
public partial class OfficeLauncherWindow : Window
{
    private OfficeDoc? _picked;

    public OfficeLauncherWindow()
    {
        AvaloniaXamlLoader.Load(this);
        LoadOpenDocuments();
    }

    /// <summary>창을 띄우고, 사용자가 선택한 열린 문서를 반환한다(앱 실행/취소 시 null).</summary>
    public async Task<OfficeDoc?> PickAsync(Window owner)
    {
        await ShowDialog(owner);
        return _picked;
    }

    private async void LoadOpenDocuments()
    {
        var docs = await OfficeWindowLister.ListOpenDocumentsAsync() ?? System.Array.Empty<OfficeDoc>();
        var list = this.FindControl<ListBox>("OpenList")!;
        var empty = this.FindControl<TextBlock>("EmptyLabel")!;

        list.ItemsSource = docs;
        var any = docs.Count > 0;
        list.IsVisible = any;
        empty.IsVisible = !any;
    }

    // 열린 문서 선택 = 그 문서로 세션 시작(활성화·채팅은 메인 VM 이 담당).
    private void OnOpenDocSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: OfficeDoc doc })
        {
            _picked = doc;
            Close();
        }
    }

    private void OnNewDoc(object? sender, RoutedEventArgs e) =>
        this.FindControl<StackPanel>("AppButtons")!.IsVisible = true;

    private void OnLaunchPowerPoint(object? sender, RoutedEventArgs e) => Launch("PowerPoint");

    private void OnLaunchWord(object? sender, RoutedEventArgs e) => Launch("Word");

    private void OnLaunchExcel(object? sender, RoutedEventArgs e) => Launch("Excel");

    private void Launch(string app)
    {
        OfficeWindowLister.Launch(app);
        Close();
    }
}
