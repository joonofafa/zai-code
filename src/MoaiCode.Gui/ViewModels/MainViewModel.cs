using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoaiCode.Gui.Agent;

namespace MoaiCode.Gui.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IAgentBackend _backend = new StubAgentBackend();

    public ObservableCollection<ChatItem> Items { get; } = new();
    public ObservableCollection<SessionItem> Sessions { get; } = new();
    public ObservableCollection<string> QuickActions { get; } = new()
    {
        "📝 보고서 만들기", "📊 표·차트 엑셀", "📑 발표자료", "🔎 문서함 검색",
    };

    [ObservableProperty] private string _input = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _isBusy;

    public MainViewModel() => Seed();

    private bool CanSend() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task Send()
    {
        var text = Input?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        Input = string.Empty;
        Items.Add(new UserItem { Text = text });
        IsBusy = true;

        AssistantItem? assistant = null;
        ActivityItem? activity = null;
        await foreach (var ev in _backend.SendAsync(text, CancellationToken.None))
        {
            switch (ev)
            {
                case ActivityStarted s:
                    activity = new ActivityItem { Text = s.Text };
                    Items.Add(activity);
                    break;
                case ActivityDone d:
                    if (activity is not null) { activity.Text = d.Text; activity.Done = true; }
                    break;
                case DocumentProduced doc:
                    Items.Add(new DocumentItem { Icon = doc.Icon, Kind = doc.Kind, FileName = doc.FileName, Path = doc.Path });
                    break;
                case AssistantDelta a:
                    assistant ??= AddAssistant();
                    assistant.Text += a.Text;
                    break;
                case TurnDone:
                    break;
            }
        }

        IsBusy = false;
    }

    private AssistantItem AddAssistant()
    {
        var a = new AssistantItem();
        Items.Add(a);
        return a;
    }

    [RelayCommand]
    private void Quick(string action)
    {
        Input = action.Length > 2 ? action[2..].Trim() + " 만들어줘" : action;
        if (SendCommand.CanExecute(null))
        {
            SendCommand.Execute(null);
        }
    }

    // 화면을 살아있게 — 초기 예시 대화 + 이전 작업.
    private void Seed()
    {
        Sessions.Add(new SessionItem { Title = "카페 매출 TOP10 엑셀", When = "오늘" });
        Sessions.Add(new SessionItem { Title = "금융권 AI 거버넌스 보고서", When = "어제" });
        Sessions.Add(new SessionItem { Title = "메달리온 발표자료", When = "7월 20일" });

        Items.Add(new AssistantItem
        {
            Text = "안녕하세요! 무엇을 만들어 드릴까요?\n" +
                   "예) \"2025 카페 브랜드 매출 TOP10을 표와 차트가 있는 엑셀로 만들어줘\"",
        });
        Items.Add(new UserItem { Text = "2025 카페 브랜드 매출 TOP10 표+차트 엑셀 만들어줘" });
        Items.Add(new ActivityItem { Text = "엑셀 문서 생성 완료", Done = true });
        Items.Add(new DocumentItem { Icon = "📊", Kind = "엑셀", FileName = "카페매출_2025.xlsx", Path = "내 문서\\MoAI\\카페매출_2025.xlsx" });
        Items.Add(new AssistantItem
        {
            Text = "브랜드별 누적매출 TOP10을 표로 정리하고 막대 차트를 넣었어요. " +
                   "[열기]로 확인하시고, 회사 문서함에 올리려면 [문서함 올리기]를 누르세요.",
        });
    }
}
