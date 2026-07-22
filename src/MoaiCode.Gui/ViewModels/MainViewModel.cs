using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoaiCode.Core.Tools;
using MoaiCode.Gui.Agent;
using MoaiCode.Tools.OpenXml;

namespace MoaiCode.Gui.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IAgentBackend _backend;
    private readonly bool _live;

    public ObservableCollection<ChatItem> Items { get; } = new();
    public ObservableCollection<SessionItem> Sessions { get; } = new();

    /// <summary>모드 B: 이번 작업에 첨부된 참조 문서(로컬/조직).</summary>
    public ObservableCollection<ReferenceItem> References { get; } = new();
    public bool HasReferences => References.Count > 0;
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
        Items.Add(new UserItem { Text = References.Count > 0 ? $"{text}\n\n📎 참조 {References.Count}개" : text });
        IsBusy = true;

        var prompt = ComposePrompt(text);
        AssistantItem? assistant = null;
        ActivityItem? activity = null;
        await foreach (var ev in _backend.SendAsync(prompt, CancellationToken.None))
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

    // 문서당 컨텍스트 주입 상한(대형 단일 문서 방어; 초과분 축약은 TODO — 로컬 검색으로 대체 예정).
    private const int PerRefCharCap = 30000;

    /// <summary>로컬 파일을 참조로 첨부(원문 추출). View 의 파일 선택기에서 호출.</summary>
    public void AddReference(string path)
    {
        if (References.Any(r => r.Path == path) || !DocumentTextExtractor.IsSupported(path))
        {
            return;
        }

        string text;
        try
        {
            text = DocumentTextExtractor.Extract(path);
        }
        catch
        {
            return; // 파싱 실패 파일은 조용히 건너뜀
        }

        References.Add(new ReferenceItem
        {
            DisplayName = System.IO.Path.GetFileName(path),
            Source = "local",
            Text = text,
            Path = path,
        });
        OnPropertyChanged(nameof(HasReferences));
    }

    /// <summary>조직 문서함 검색 결과(청크)를 참조로 첨부. View 의 검색 다이얼로그에서 호출.</summary>
    public void AddOrgReference(OrgHit hit)
    {
        if (References.Any(r => r.Source == "org" && r.Path == hit.DocumentId && r.Text == hit.Snippet))
        {
            return;
        }

        References.Add(new ReferenceItem
        {
            DisplayName = hit.Title,
            Source = "org",
            Text = hit.Snippet,
            Path = hit.DocumentId,
        });
        OnPropertyChanged(nameof(HasReferences));
    }

    [RelayCommand]
    private void RemoveReference(ReferenceItem item)
    {
        References.Remove(item);
        OnPropertyChanged(nameof(HasReferences));
    }

    // 첨부 참조가 있으면 원문을 프롬프트 앞에 붙여 넣는다(모드 B: 청킹 없이 통째로).
    private string ComposePrompt(string userText)
    {
        if (References.Count == 0)
        {
            return userText;
        }

        var sb = new StringBuilder();
        sb.AppendLine("아래 참조 문서를 근거로 작업하세요. 관련 있는 내용만 활용하고, 문서에 없는 사실을 지어내지 마세요.");
        sb.AppendLine();
        var i = 1;
        foreach (var r in References)
        {
            var body = r.Text.Length > PerRefCharCap ? r.Text[..PerRefCharCap] + "\n…(이하 생략)" : r.Text;
            var src = r.Source == "org" ? "조직 문서함" : "로컬 파일";
            sb.AppendLine($"─── 참조 {i} · {r.DisplayName} ({src}) ───");
            sb.AppendLine(body);
            sb.AppendLine();
            i++;
        }

        sb.AppendLine("─── 요청 ───");
        sb.Append(userText);
        return sb.ToString();
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
