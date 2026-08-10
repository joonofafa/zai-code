using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoaiCode.Config;
using MoaiCode.Gui.Agent;
using MoaiCode.Localization;
using MoaiCode.Tools.OpenXml;

namespace MoaiCode.Gui.ViewModels;

/// <summary>'내 로컬 폴더' 검색 창의 VM — 연결된 로컬 폴더들의 로컬 인덱스(.moai-chunks)를 코사인 검색.
/// 조직 문서함 검색(OrgSearchViewModel)과 동일한 레이아웃/흐름, 대상만 로컬.</summary>
public sealed partial class LocalSearchViewModel : ObservableObject
{
    [ObservableProperty] private string _query = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private bool _busy;

    [ObservableProperty] private string _status = L10n.Get("gui.search.enterHint");

    public ObservableCollection<LocalHitVM> Results { get; } = new();

    private bool CanSearch() => !Busy;

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task Search()
    {
        var q = Query?.Trim();
        if (string.IsNullOrEmpty(q))
        {
            return;
        }

        var folders = GuiSettings.Load().ConnectedFolders
            .Select(f => f.Path)
            .Where(Directory.Exists)
            .ToList();

        if (folders.Count == 0)
        {
            Status = L10n.Get("gui.search.noLocalFolders");
            return;
        }

        Busy = true;
        Status = L10n.Get("gui.search.searching");
        Results.Clear();
        GuiBootstrap.EnsureEnvReady();

        try
        {
            var all = new List<LocalHit>();
            foreach (var folder in folders)
            {
                try
                {
                    all.AddRange(await LocalSearch.SearchAsync(folder, q, 8, CancellationToken.None).ConfigureAwait(true));
                }
                catch (Exception ex)
                {
                    MoaiLog.Warn($"LocalSearch: folder search failed: {ex.GetType().Name}");
                }
            }

            foreach (var h in all.OrderByDescending(h => h.Score).Take(20))
            {
                Results.Add(new LocalHitVM(h));
            }

            Status = Results.Count == 0
                ? L10n.Get("gui.search.noResultsLocal")
                : L10n.Get("gui.search.countFmt", Results.Count);
        }
        catch (Exception ex)
        {
            Status = L10n.Get("gui.search.errorFmt", ex.Message);
        }
        finally
        {
            Busy = false;
        }
    }
}

/// <summary>로컬 검색 결과 한 건(체크박스 바인딩용).</summary>
public sealed partial class LocalHitVM : ObservableObject
{
    public LocalHitVM(LocalHit hit) => Hit = hit;

    public LocalHit Hit { get; }

    [ObservableProperty] private bool _isSelected;

    public string Title => $"{Hit.Source} #{Hit.Index}";

    public string Snippet => Hit.Text;

    public string ScoreText => $"{Hit.Score:0.00}";
}
