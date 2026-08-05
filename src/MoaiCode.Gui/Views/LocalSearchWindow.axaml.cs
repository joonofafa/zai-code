using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using MoaiCode.Gui.Agent;
using MoaiCode.Gui.ViewModels;

namespace MoaiCode.Gui.Views;

public partial class LocalSearchWindow : Window
{
    private List<PickedRef>? _selected;

    public LocalSearchWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = new LocalSearchViewModel();
    }

    /// <summary>다이얼로그를 띄우고 사용자가 첨부한 참조를 돌려준다(닫기 시 null).</summary>
    public async Task<List<PickedRef>?> PickAsync(Window owner)
    {
        await ShowDialog(owner);
        return _selected;
    }

    private void OnAttach(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LocalSearchViewModel vm)
        {
            Close();
            return;
        }

        // 로컬 스니펫을 참조로. DocumentId 자리에 source(로컬 식별자)를 넣고 WholeDoc=false.
        _selected = vm.Results
            .Where(r => r.IsSelected)
            .Select(r => new PickedRef(r.Hit.Source, r.Hit.Text, r.Hit.Source, WholeDoc: false))
            .ToList();
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
