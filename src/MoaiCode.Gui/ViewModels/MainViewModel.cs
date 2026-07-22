using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoaiCode.Core.Tools;
using MoaiCode.Gui.Agent;

namespace MoaiCode.Gui.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IAgentBackend _backend;
    private readonly bool _live;

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

    public MainViewModel()
    {
        // 실제 코어 임베드. 자격증명 없으면 데모(stub) 로 폴백.
        var engine = GuiBootstrap.TryBuild(new AutoApproveGate(), out var ws, out var error);
        if (engine is not null)
        {
            _backend = new EngineAgentBackend(engine, ws);
            _live = true;
        }
        else
        {
            _backend = new StubAgentBackend();
            _live = false;
        }

        Seed(error);
    }

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
                    assistant = null; // 이후 답변은 새 말풍선으로(순서 유지)
                    activity = new ActivityItem { Text = s.Text };
                    Items.Add(activity);
                    break;
                case ActivityDone d:
                    if (activity is not null) { activity.Text = d.Text; activity.Done = true; }
                    break;
                case DocumentProduced doc:
                    assistant = null;
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

    private void Seed(string? error)
    {
        Sessions.Add(new SessionItem { Title = "카페 매출 TOP10 엑셀", When = "오늘" });
        Sessions.Add(new SessionItem { Title = "금융권 AI 거버넌스 보고서", When = "어제" });
        Sessions.Add(new SessionItem { Title = "메달리온 발표자료", When = "7월 20일" });

        if (_live)
        {
            Items.Add(new AssistantItem
            {
                Text = "안녕하세요! 무엇을 만들어 드릴까요?\n" +
                       "예) \"2025 카페 브랜드 매출 TOP10을 표와 차트가 있는 엑셀로 만들어줘\"",
            });
        }
        else
        {
            Items.Add(new AssistantItem
            {
                Text = "데모 모드입니다 — 아직 로그인되어 있지 않아 실제 생성은 안 돼요.\n" +
                       (error ?? "`moai login` 으로 로그인하면 실제로 문서를 만들어 드립니다.") +
                       "\n지금은 입력해 보시면 흐름만 재현합니다.",
            });
        }
    }
}
