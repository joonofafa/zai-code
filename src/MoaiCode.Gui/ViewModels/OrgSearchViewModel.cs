using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoaiCode.Gui.Agent;

namespace MoaiCode.Gui.ViewModels;

/// <summary>조직 문서함 검색 다이얼로그. 결과(청크)를 선택해 참조로 첨부한다.</summary>
public sealed partial class OrgSearchViewModel : ObservableObject
{
    [ObservableProperty] private string _query = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private bool _busy;

    [ObservableProperty] private string _status = "검색어를 입력하고 Enter.";

    public ObservableCollection<OrgHitVM> Results { get; } = new();

    private bool CanSearch() => !Busy;

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task Search()
    {
        var q = Query?.Trim();
        if (string.IsNullOrEmpty(q))
        {
            return;
        }

        Busy = true;
        Status = "검색 중…";
        Results.Clear();

        var (hits, err) = await OrgSearchClient.SearchAsync(q, CancellationToken.None);
        if (err is not null)
        {
            Status = err;
        }
        else
        {
            foreach (var h in hits)
            {
                Results.Add(new OrgHitVM(h));
            }

            Status = hits.Count == 0 ? "결과 없음 — 다른 검색어를 시도하세요." : $"{hits.Count}건 · 첨부할 항목을 선택하세요.";
        }

        Busy = false;
    }
}

/// <summary>검색 결과 한 건(선택 체크 포함).</summary>
public sealed partial class OrgHitVM : ObservableObject
{
    public OrgHitVM(OrgHit hit) => Hit = hit;

    public OrgHit Hit { get; }

    [ObservableProperty] private bool _isSelected;

    public string Title => Hit.Title;
    public string Snippet => Hit.Snippet;
    public string ScoreText => $"{Hit.Score:0.00}";
}
