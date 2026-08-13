using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using MoaiCode.Gui.Agent;
using MoaiCode.Gui.ViewModels;
using MoaiCode.Localization;

namespace MoaiCode.Gui.Views;

public partial class OrgSearchWindow : Window
{
    private List<PickedRef>? _selected;

    public OrgSearchWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = new OrgSearchViewModel();
    }

    /// <summary>다이얼로그를 띄우고 사용자가 첨부한 참조를 돌려준다(닫기 시 null).</summary>
    public async Task<List<PickedRef>?> PickAsync(Window owner)
    {
        await ShowDialog(owner);
        return _selected;
    }

    private async void OnAttach(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OrgSearchViewModel vm)
        {
            Close();
            return;
        }

        var chosen = vm.Results.Where(r => r.IsSelected).Select(r => r.Hit).ToList();
        if (chosen.Count == 0)
        {
            _selected = new List<PickedRef>();
            Close();
            return;
        }

        // 스니펫 모드: 선택한 청크를 그대로.
        if (!vm.WholeDoc)
        {
            _selected = chosen.Select(h => new PickedRef(h.Title, h.Snippet, h.DocumentId, WholeDoc: false)).ToList();
            Close();
            return;
        }

        // 원문 통째: 문서ID 중복 제거 후 다운로드→추출.
        vm.Busy = true;
        var byDoc = chosen.GroupBy(h => h.DocumentId).Select(g => g.First()).ToList();
        var result = new List<PickedRef>();
        foreach (var h in byDoc)
        {
            vm.Status = L10n.Get("gui.orgsearch.fetchingFmt", h.Title);
            var (text, err) = await OrgSearchClient.DownloadDocTextAsync(h.DocumentId, CancellationToken.None);
            if (err is null && !string.IsNullOrWhiteSpace(text))
            {
                result.Add(new PickedRef(h.Title, text!, h.DocumentId, WholeDoc: true));
            }
            else
            {
                vm.Status = L10n.Get("gui.orgsearch.failedFmt", h.Title, err);
            }
        }

        vm.Busy = false;
        if (result.Count == 0)
        {
            vm.Status = L10n.Get("gui.orgsearch.fetchFailed");
            return; // 다이얼로그 유지
        }

        _selected = result;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
