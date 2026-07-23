using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoaiCode.Config;
using MoaiCode.Core.Tools;
using MoaiCode.Gui.Agent;
using MoaiCode.Tools.OpenXml;

namespace MoaiCode.Gui.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private IAgentBackend _backend = null!;
    private bool _live;
    private string? _lastError;

    /// <summary>사이드바 표시용 앱 버전(예: v0.4.0).</summary>
    public string AppVersion { get; } =
        "v" + (typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.4.0");

    [ObservableProperty] private string _accountLabel = "로그인 필요";

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
        BuildBackend();
        RefreshAccount();
        Seed(_lastError);
    }

    // 실제 코어 임베드. 자격증명 없으면 데모(stub) 로 폴백.
    private void BuildBackend()
    {
        var engine = GuiBootstrap.TryBuild(new AutoApproveGate(), out var ws, out var error);
        _lastError = error;
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
    }

    /// <summary>로그인 후 호출 — 새 자격증명으로 엔진을 다시 만들고 상태를 갱신한다.</summary>
    public void ReloadBackend()
    {
        BuildBackend();
        RefreshAccount();
        Items.Add(new AssistantItem
        {
            Text = _live
                ? "✅ 로그인이 적용됐어요. 이제 문서를 만들어 드릴 수 있어요."
                : "로그인 정보를 확인하지 못했어요. 사이드바의 '로그인 / 계정'에서 다시 시도해 주세요.",
        });
    }

    private void RefreshAccount()
    {
        var acc = SettingsLoader.Load(System.IO.Directory.GetCurrentDirectory()).Account;
        AccountLabel = string.IsNullOrWhiteSpace(acc) ? "로그인 필요" : "👤 " + acc;
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

        try
        {
            // 엔진(네트워크 + 도구 실행)은 백그라운드 스레드에서 — 차트/문서 생성 같은 무거운
            // 동기 작업이 UI 스레드를 막지 않도록. UI 변경만 Dispatcher 로 마샬링.
            await Task.Run(async () =>
            {
                await foreach (var ev in _backend.SendAsync(prompt, CancellationToken.None).ConfigureAwait(false))
                {
                    var current = ev;
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        switch (current)
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
                    });
                }
            });
        }
        catch (Exception ex)
        {
            Items.Add(new AssistantItem { Text = "⚠️ 처리 중 오류가 발생했어요: " + ex.Message });
        }
        finally
        {
            IsBusy = false;
        }
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

    /// <summary>조직 문서함 참조(스니펫 또는 원문)를 첨부. View 의 검색 다이얼로그에서 호출.</summary>
    public void AddOrgReference(PickedRef r)
    {
        if (References.Any(x => x.Source == "org" && x.Path == r.DocumentId && x.Text == r.Text))
        {
            return;
        }

        References.Add(new ReferenceItem
        {
            DisplayName = r.WholeDoc ? r.Title + " (원문)" : r.Title,
            Source = "org",
            Text = r.Text,
            Path = r.DocumentId,
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
                Text = "아직 로그인되어 있지 않아요.\n" +
                       "왼쪽 아래 **'로그인 / 계정 설정'** 을 눌러 로그인하면 실제로 문서를 만들어 드립니다." +
                       (string.IsNullOrWhiteSpace(error) ? "" : "\n\n(참고: " + error + ")"),
            });
        }
    }
}
