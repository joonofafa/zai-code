using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using MoaiCode.Gui.Agent;
using MoaiCode.Gui.ViewModels;

namespace MoaiCode.Gui.Views;

public partial class OrgSearchWindow : Window
{
    private List<OrgHit>? _selected;

    public OrgSearchWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = new OrgSearchViewModel();
    }

    /// <summary>다이얼로그를 띄우고 사용자가 첨부한 항목을 돌려준다(닫기 시 null).</summary>
    public async Task<List<OrgHit>?> PickAsync(Window owner)
    {
        await ShowDialog(owner);
        return _selected;
    }

    private void OnAttach(object? sender, RoutedEventArgs e)
    {
        if (DataContext is OrgSearchViewModel vm)
        {
            _selected = vm.Results.Where(r => r.IsSelected).Select(r => r.Hit).ToList();
        }

        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
