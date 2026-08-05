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

    [ObservableProperty] private string _status = "검색어를 입력하고 Enter.";

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
            Status = "인덱싱된 로컬 폴더가 없습니다. '로컬 폴더' 탭에서 폴더를 연결하세요.";
            return;
        }

        Busy = true;
        Status = "검색 중…";
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
                ? "결과 없음 — 다른 검색어를 시도하거나 폴더가 인덱싱됐는지 확인하세요."
                : $"{Results.Count}건 · 첨부할 항목을 선택하세요.";
        }
        catch (Exception ex)
        {
            Status = "검색 오류: " + ex.Message;
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
