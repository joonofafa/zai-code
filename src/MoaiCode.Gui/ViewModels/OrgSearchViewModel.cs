using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoaiCode.Gui.Agent;
using MoaiCode.Localization;

namespace MoaiCode.Gui.ViewModels;

/// <summary>조직 문서함 검색 다이얼로그. 결과(청크)를 선택해 참조로 첨부한다.</summary>
public sealed partial class OrgSearchViewModel : ObservableObject
{
    [ObservableProperty] private string _query = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private bool _busy;

    [ObservableProperty] private string _status = L10n.Get("gui.search.enterHint");

    /// <summary>true 면 스니펫 대신 문서 원문 전체를 첨부.</summary>
    [ObservableProperty] private bool _wholeDoc;

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
        Status = L10n.Get("gui.search.searching");
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

            Status = hits.Count == 0
                ? L10n.Get("gui.search.noResultsOrg")
                : L10n.Get("gui.search.countFmt", hits.Count);
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
