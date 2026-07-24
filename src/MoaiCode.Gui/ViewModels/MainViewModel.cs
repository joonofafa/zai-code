using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoaiCode.Config;
using MoaiCode.Core.Tools;
using MoaiCode.Gui.Agent;
using MoaiCode.Gui.Sessions;
using MoaiCode.Localization;
using MoaiCode.Tools.OpenXml;
using MoaiCode.Tools.Office;

namespace MoaiCode.Gui.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private IAgentBackend _backend = null!;
    private bool _live;

    /// <summary>사이드바 표시용 앱 버전(예: v0.4.0).</summary>
    public string AppVersion { get; } =
        "v" + (typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.4.0");

    [ObservableProperty] private string _accountLabel = "로그인 필요";
    [ObservableProperty] private string _accountEmail = string.Empty;

    /// <summary>인증 여부. false 면 창 전체가 로그인 화면.</summary>
    [ObservableProperty] private bool _isAuthed;

    /// <summary>우측 패널이 설정 화면을 표시 중인지(false=채팅).</summary>
    [ObservableProperty] private bool _showSettings;

    /// <summary>우측 패널이 홈(시작) 화면을 표시 중인지. 로그인 직후·새 작업 시 true.</summary>
    [ObservableProperty] private bool _showHome = true;

    /// <summary>우측 패널 표시 상태: 설정 &gt; 홈 &gt; 채팅 순 우선.</summary>
    public bool IsHomeVisible => ShowHome && !ShowSettings;
    public bool IsChatVisible => !ShowHome && !ShowSettings;

    partial void OnShowHomeChanged(bool value) => NotifyPanelVisibility();
    partial void OnShowSettingsChanged(bool value) => NotifyPanelVisibility();

    private void NotifyPanelVisibility()
    {
        OnPropertyChanged(nameof(IsHomeVisible));
        OnPropertyChanged(nameof(IsChatVisible));
    }

    /// <summary>로그인 화면(창 내 임베드)용 서브 VM.</summary>
    public LoginViewModel Login { get; }

    /// <summary>설정: 모델 선택.</summary>
    public ObservableCollection<string> SettingsModels { get; } = new();
    public IReadOnlyList<LanguageOption> SettingsLanguages => L10n.SupportedLanguages;
    [ObservableProperty] private string? _settingsModel;
    [ObservableProperty] private LanguageOption? _settingsLanguage;
    [ObservableProperty] private bool _isDark = true;
    [ObservableProperty] private bool _confirmLogout;

    public string SettingsTitleText => L10n.Get("gui.settings.title");
    public string SettingsAccountText => L10n.Get("gui.settings.account");
    public string SettingsLogoutText => L10n.Get("gui.settings.logout");
    public string SettingsLogoutConfirmText => L10n.Get("gui.settings.logoutConfirm");
    public string SettingsCancelText => L10n.Get("gui.settings.cancel");
    public string SettingsApplyText => L10n.Get("gui.settings.apply");
    public string SettingsModelText => L10n.Get("gui.settings.model");
    public string SettingsModelHintText => L10n.Get("gui.settings.modelHint");
    public string SettingsLanguageText => L10n.Get("gui.settings.language");
    public string SettingsLanguageHintText => L10n.Get("gui.settings.languageHint");
    public string SettingsThemeText => L10n.Get("gui.settings.theme");
    public string SettingsDarkModeText => L10n.Get("gui.settings.darkMode");
    public string SettingsLightModeText => L10n.Get("gui.settings.lightMode");

    /// <summary>좌패널 상단: 열린 Office 문서(런처 — 클릭 시 활성 대상으로 바인딩).</summary>
    public ObservableCollection<OfficeDoc> OfficeDocs { get; } = new();
    [ObservableProperty] private OfficeDoc? _selectedOfficeDoc;

    /// <summary>열린 문서 목록이 비었는지(빈 상태 1줄 안내용).</summary>
    public bool HasOpenDocs => OfficeDocs.Count > 0;

    // ── 활성 대상 문서(입력창 위 컨텍스트 칩) ──
    /// <summary>현재 세션이 열린 Office 문서에 연결(편집 모드)돼 있는지.</summary>
    [ObservableProperty] private bool _isDocConnected;

    /// <summary>칩에 표시할 활성 대상 라벨(예: "보고서.pptx").</summary>
    [ObservableProperty] private string _activeDocName = string.Empty;

    /// <summary>활성 대상 앱(PowerPoint/Word/Excel) — 칩 보조 표기.</summary>
    [ObservableProperty] private string _activeDocApp = string.Empty;

    /// <summary>편집 적용 범위 선택지.</summary>
    public ObservableCollection<string> ActiveScopes { get; } =
        new() { "현재 선택 영역", "문서 전체" };

    [ObservableProperty] private string _activeScope = "현재 선택 영역";

    // 현재 세션 분류(저장 메타). generate 는 문서 생성이 일어나면 승격.
    private string _sessionKind = "chat";
    private string? _sessionTargetDoc;

    /// <summary>채팅을 맨 아래로 스크롤하도록 View 에 요청(자동 스크롤).</summary>
    public event Action? ScrollToEndRequested;

    private void RequestScroll() => ScrollToEndRequested?.Invoke();

    public ObservableCollection<ChatItem> Items { get; } = new();

    /// <summary>좌패널 하단: 대화 기록.</summary>
    public ObservableCollection<SessionMeta> Sessions { get; } = new();

    /// <summary>모드 B: 이번 작업에 첨부된 참조 문서(로컬/조직).</summary>
    public ObservableCollection<ReferenceItem> References { get; } = new();
    public bool HasReferences => References.Count > 0;

    [ObservableProperty] private string _input = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _isBusy;

    private string _sessionId = NewSessionId();
    private readonly List<TurnLine> _transcript = new();

    public MainViewModel()
    {
        Login = new LoginViewModel();
        Login.LoggedIn += OnLoggedIn;
        _isDark = !IsLight(GuiSettings.Load().Theme);

        if (HasCredential())
        {
            IsAuthed = true;
            BuildBackend();
            RefreshAccount();
            RefreshSessions();
            RefreshOffice();
            ShowHome = true; // 시작은 홈(새 작업) 화면
        }
        else
        {
            IsAuthed = false; // 로그인 뷰 표시
        }
    }

    private static bool HasCredential() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"))
        || !string.IsNullOrWhiteSpace(new FileCredentialStore().Get("OPENAI_API_KEY"));

    private static bool IsLight(string? theme) =>
        string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase);

    private static string NewSessionId() => DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff");

    private void OnLoggedIn() => Dispatcher.UIThread.Post(() =>
    {
        IsAuthed = true;
        ShowSettings = false;
        Login.ResetToLogin();
        BuildBackend();
        RefreshAccount();
        RefreshSessions();
        RefreshOffice();
        Items.Clear();
        _transcript.Clear();
        _sessionId = NewSessionId();
        ShowHome = true; // 로그인 직후 홈 화면
    });

    // ── 설정 패널 ──
    [RelayCommand]
    private void OpenSettings()
    {
        var s = SettingsLoader.Load(System.IO.Directory.GetCurrentDirectory());
        AccountEmail = s.Account ?? string.Empty;
        SettingsModels.Clear();
        foreach (var m in (s.AvailableModels ?? string.Empty)
                 .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            SettingsModels.Add(m);
        }

        if (!string.IsNullOrEmpty(s.Model) && !SettingsModels.Contains(s.Model))
        {
            SettingsModels.Insert(0, s.Model);
        }

        SettingsModel = s.Model ?? SettingsModels.FirstOrDefault();
        SettingsLanguage = SettingsLanguages.FirstOrDefault(x => x.Code == s.Language)
                           ?? SettingsLanguages.First(x => x.Code == L10n.DefaultLanguage);
        IsDark = !IsLight(GuiSettings.Load().Theme);
        ConfirmLogout = false;
        ShowSettings = true;
    }

    [RelayCommand]
    private void ApplySettings()
    {
        var values = new Dictionary<string, string?>();
        if (!string.IsNullOrEmpty(SettingsModel))
        {
            Environment.SetEnvironmentVariable("MOAI_MODEL", SettingsModel);
            values["model"] = SettingsModel;
        }

        if (SettingsLanguage is not null)
        {
            L10n.SetLanguage(SettingsLanguage.Code);
            Environment.SetEnvironmentVariable("MOAI_LANGUAGE", SettingsLanguage.Code);
            values["language"] = SettingsLanguage.Code;
            NotifyLocalizedSettingsProperties();
        }

        if (values.Count > 0)
        {
            SettingsWriter.Set(values);
        }

        var gs = GuiSettings.Load();
        gs.Theme = IsDark ? "dark" : "light";
        gs.Save();
        if (Application.Current is not null)
        {
            Application.Current.RequestedThemeVariant = IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        }

        ReloadBackend();
        ShowSettings = false;
    }

    private void NotifyLocalizedSettingsProperties()
    {
        OnPropertyChanged(nameof(SettingsTitleText));
        OnPropertyChanged(nameof(SettingsAccountText));
        OnPropertyChanged(nameof(SettingsLogoutText));
        OnPropertyChanged(nameof(SettingsLogoutConfirmText));
        OnPropertyChanged(nameof(SettingsCancelText));
        OnPropertyChanged(nameof(SettingsApplyText));
        OnPropertyChanged(nameof(SettingsModelText));
        OnPropertyChanged(nameof(SettingsModelHintText));
        OnPropertyChanged(nameof(SettingsLanguageText));
        OnPropertyChanged(nameof(SettingsLanguageHintText));
        OnPropertyChanged(nameof(SettingsThemeText));
        OnPropertyChanged(nameof(SettingsDarkModeText));
        OnPropertyChanged(nameof(SettingsLightModeText));
    }

    [RelayCommand]
    private void CloseSettings() => ShowSettings = false;

    [RelayCommand]
    private void Logout() => ConfirmLogout = true;

    [RelayCommand]
    private void CancelLogout() => ConfirmLogout = false;

    [RelayCommand]
    private void LogoutConfirmed()
    {
        SaveCurrent();
        new FileCredentialStore().Set("OPENAI_API_KEY", string.Empty);
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", string.Empty);
        SettingsWriter.Set(new Dictionary<string, string?>
        {
            ["baseUrl"] = null, ["account"] = null, ["loginAt"] = null, ["name"] = null,
        });

        ConfirmLogout = false;
        ShowSettings = false;
        Items.Clear();
        _transcript.Clear();
        AccountLabel = "로그인 필요";
        Login.ResetToLogin();
        IsAuthed = false;
    }

    // ── 좌패널: Office 창 ──
    [RelayCommand]
    private void RefreshOffice()
    {
        OfficeDocs.Clear();
        foreach (var d in OfficeWindowLister.ListOpenDocuments())
        {
            OfficeDocs.Add(d);
        }

        OnPropertyChanged(nameof(HasOpenDocs));
    }

    partial void OnSelectedOfficeDocChanged(OfficeDoc? value)
    {
        if (value is not null)
        {
            OfficeWindowLister.Activate(value);
        }
    }

    /// <summary>
    /// 홈의 "새로운 오피스 문서 작업"에서 고른 열린 문서로 세션을 시작한다.
    /// COM 으로 해당 문서를 활성화(편집 툴 대상 지정)하고, 채팅 화면을 연다.
    /// </summary>
    public void OpenOfficeSession(OfficeDoc doc)
    {
        if (IsBusy)
        {
            return;
        }

        StartFreshSession();

        // 좌패널 목록에도 반영하고 활성화(OnSelectedOfficeDocChanged 가 Activate 호출).
        if (!OfficeDocs.Contains(doc))
        {
            OfficeDocs.Add(doc);
            OnPropertyChanged(nameof(HasOpenDocs));
        }

        SelectedOfficeDoc = doc;
        var connected = OfficeWindowLister.Activate(doc);

        // 활성 대상 바인딩(세션 = 편집 모드). 칩으로 표시.
        BindActiveDoc(doc);

        ShowSettings = false;
        ShowHome = false; // 채팅 화면 표시

        if (connected)
        {
            Items.Add(new AssistantItem { Text = $"**{doc.Display}** 에 연결했어요. 무엇을 할까요?" });
            Items.Add(new SuggestionItem { Suggestions = SuggestionsFor(doc.App) });
        }
        else
        {
            Items.Add(new AssistantItem
            {
                Text = $"**{doc.Display}** 에 연결을 시도했지만 응답이 없어요. " +
                       "문서가 아직 열려 있는지 확인한 뒤 다시 시도해 주세요.",
            });
        }

        RequestScroll();
    }

    // 편집 세션 진입 시 앱별 추천 질문(클릭 버튼).
    private static IReadOnlyList<string> SuggestionsFor(string app) => app switch
    {
        "PowerPoint" => new[]
        {
            "현재 슬라이드 내용을 더 풍성하게 만들어줘",
            "선택한 도형 배경색을 파란색으로 바꿔줘",
            "표지 디자인을 더 깔끔하게 다듬어줘",
        },
        "Excel" => new[]
        {
            "선택한 표를 요약해줘",
            "이 데이터로 차트를 제안해줘",
            "머리글 행을 굵게 강조해줘",
        },
        "Word" => new[]
        {
            "이 문단을 더 간결하게 다듬어줘",
            "제목 스타일을 정리해줘",
            "맞춤법과 문장을 매끄럽게 고쳐줘",
        },
        _ => new[] { "이 문서를 요약해줘", "개선할 점을 알려줘" },
    };

    /// <summary>추천 질문 버튼 클릭 → 해당 질문으로 바로 전송.</summary>
    [RelayCommand]
    private async Task UseSuggestion(string? question)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(question))
        {
            return;
        }

        Input = question;
        await Send();
    }

    /// <summary>사이드바 '열린 문서' 런처에서 문서를 클릭 → 그 문서로 편집 세션 시작.</summary>
    [RelayCommand]
    private void PickOpenDoc(OfficeDoc? doc)
    {
        if (doc is not null)
        {
            OpenOfficeSession(doc);
        }
    }

    private void BindActiveDoc(OfficeDoc doc)
    {
        _sessionKind = "edit";
        _sessionTargetDoc = doc.Name;
        ActiveDocName = doc.Name;
        ActiveDocApp = doc.App;
        IsDocConnected = true;
    }

    /// <summary>활성 대상 칩의 [✕] — 문서 연결을 해제하고 일반/생성 모드로 되돌린다.</summary>
    [RelayCommand]
    private void DetachDoc()
    {
        IsDocConnected = false;
        ActiveDocName = string.Empty;
        ActiveDocApp = string.Empty;
        _sessionTargetDoc = null;
        if (_sessionKind == "edit")
        {
            _sessionKind = "chat";
        }
    }

    // ── 좌패널: 대화 기록 ──
    private void RefreshSessions()
    {
        Sessions.Clear();
        foreach (var m in SessionStore.List())
        {
            Sessions.Add(m);
        }
    }

    private void SaveCurrent()
    {
        if (_transcript.Count > 0)
        {
            SessionStore.Save(_sessionId, _transcript, _sessionKind, _sessionTargetDoc);
            RefreshSessions();
        }
    }

    [RelayCommand]
    private void LoadSession(SessionMeta? meta)
    {
        if (IsBusy || meta is null)
        {
            return;
        }

        SaveCurrent();
        ShowHome = false; // 채팅 화면으로
        _sessionId = meta.Id;
        _sessionKind = meta.Kind;
        _sessionTargetDoc = meta.TargetDoc;
        _transcript.Clear();
        Items.Clear();
        foreach (var l in SessionStore.Load(meta.Id))
        {
            _transcript.Add(l);
            Items.Add(l.Role == "user" ? new UserItem { Text = l.Text } : new AssistantItem { Text = l.Text });
        }

        // 편집 세션이면 활성 대상 칩을 복원(실제 COM 재연결은 사용자가 문서를 다시 열면 유효).
        if (meta.Kind == "edit" && !string.IsNullOrWhiteSpace(meta.TargetDoc))
        {
            ActiveDocName = meta.TargetDoc!;
            ActiveDocApp = OfficeDocs.FirstOrDefault(d => d.Name == meta.TargetDoc)?.App ?? string.Empty;
            IsDocConnected = true;
        }
        else
        {
            DetachDoc();
        }

        RequestScroll();
    }

    /// <summary>
    /// 라이브 편집 전 확인(미리보기 게이트). 게이트(백그라운드 스레드)가 호출하면
    /// 채팅에 확인 카드를 띄우고 사용자가 [적용]/[취소] 를 누를 때까지 대기한다.
    /// </summary>
    public Task<bool> RequestConfirmAsync(string summary)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            Items.Add(new ConfirmItem { Text = summary, Tcs = tcs });
            RequestScroll();
        });
        return tcs.Task;
    }

    // 실제 코어 임베드. 자격증명 없으면 데모(stub) 로 폴백.
    private void BuildBackend()
    {
        var engine = GuiBootstrap.TryBuild(new GuiConfirmGate(RequestConfirmAsync), out var ws, out _);
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
                ? "로그인이 적용됐어요. 이제 문서를 만들어 드릴 수 있어요."
                : "로그인 정보를 확인하지 못했어요. 사이드바의 '로그인 / 계정'에서 다시 시도해 주세요.",
        });
    }

    private void RefreshAccount()
    {
        var s = SettingsLoader.Load(System.IO.Directory.GetCurrentDirectory());
        if (!string.IsNullOrWhiteSpace(s.Name))
        {
            AccountLabel = string.IsNullOrWhiteSpace(s.OrgName) ? s.Name! : $"{s.Name} ({s.OrgName})";
        }
        else if (!string.IsNullOrWhiteSpace(s.Account))
        {
            AccountLabel = s.Account!;
        }
        else
        {
            AccountLabel = "로그인 필요";
        }
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
        Items.Add(new UserItem { Text = References.Count > 0 ? $"{text}\n\n참조 {References.Count}개" : text });
        _transcript.Add(new TurnLine("user", text));
        IsBusy = true;

        // 프롬프트 직후 즉시 '작업 중…' 표시(네트워크/추론 대기 동안 피드백 — 프리징 오해 방지).
        var thinking = new ActivityItem { Text = "작업 중…", Done = false };
        Items.Add(thinking);
        RequestScroll();
        var thinkingRemoved = false;
        void RemoveThinking()
        {
            if (!thinkingRemoved)
            {
                thinkingRemoved = true;
                Items.Remove(thinking);
            }
        }

        var prompt = ComposePrompt(text);
        AssistantItem? assistant = null;

        // 진행 배지는 여러 도구가 병렬로 시작될 수 있으므로 큐로 관리한다(FIFO 매칭).
        // 단일 변수로 두면 나중 배지가 앞 배지를 덮어써 "작업하는 중…"이 완료되지 못하고 쌓인다.
        var pending = new List<ActivityItem>();

        try
        {
            // 엔진(네트워크 + 도구 실행)은 백그라운드 스레드에서 — 무거운 동기 작업이 UI 를 막지 않도록.
            // 스트리밍 토큰은 모아서 ≈60ms 마다만 UI 에 반영(토큰마다 갱신 시 렌더 폭주로 프리징).
            await Task.Run(async () =>
            {
                var sb = new StringBuilder();
                var sw = Stopwatch.StartNew();

                async Task FlushText()
                {
                    if (sb.Length == 0)
                    {
                        return;
                    }

                    var chunk = sb.ToString();
                    sb.Clear();
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        assistant ??= AddAssistant();
                        assistant.Text += chunk;
                        RequestScroll();
                    });
                }

                await foreach (var ev in _backend.SendAsync(prompt, CancellationToken.None).ConfigureAwait(false))
                {
                    switch (ev)
                    {
                        case AssistantDelta a:
                            sb.Append(a.Text);
                            if (sw.ElapsedMilliseconds >= 60)
                            {
                                await Dispatcher.UIThread.InvokeAsync(RemoveThinking);
                                await FlushText();
                                sw.Restart();
                            }

                            break;

                        case ActivityStarted s:
                            await FlushText();
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                RemoveThinking();
                                assistant = null; // 이후 답변은 새 말풍선으로(순서 유지)
                                var act = new ActivityItem { Text = s.Text };
                                pending.Add(act);
                                Items.Add(act);
                                RequestScroll();
                            });
                            break;

                        case ActivityDone d:
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                // 가장 먼저 시작한 미완료 배지를 완료 처리(도구는 요청 순서대로 끝난다).
                                if (pending.Count > 0)
                                {
                                    var act = pending[0];
                                    pending.RemoveAt(0);
                                    act.Text = d.Text;
                                    act.Done = true;
                                }
                            });
                            break;

                        case DocumentProduced doc:
                            await FlushText();
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                RemoveThinking();
                                assistant = null;
                                // 편집 세션이 아니면 '문서 생성' 세션으로 분류(히스토리 배지용).
                                if (_sessionKind != "edit")
                                {
                                    _sessionKind = "generate";
                                }

                                Items.Add(new DocumentItem { Icon = doc.Icon, Kind = doc.Kind, FileName = doc.FileName, Path = doc.Path });
                                RequestScroll();
                            });
                            break;

                        case TurnDone:
                            break;
                    }
                }

                await FlushText();
            });
        }
        catch (Exception ex)
        {
            var logPath = LogError(text, ex);
            Items.Add(new AssistantItem
            {
                Text = "처리 중 오류가 발생했어요. 잠시 후 다시 시도해 주세요.\n" +
                       "계속되면 좌측 하단 설정에서 모델을 바꾸거나 관리자에게 문의하세요.\n" +
                       $"자세한 내용은 오류 로그에 기록됐어요: {logPath}",
            });
        }
        finally
        {
            RemoveThinking();

            // 완료 이벤트를 못 받은 잔여 배지가 있으면 "작업하는 중…" 으로 남지 않도록 정리한다.
            foreach (var act in pending)
            {
                act.Done = true;
            }

            pending.Clear();

            if (assistant is not null && !string.IsNullOrWhiteSpace(assistant.Text))
            {
                _transcript.Add(new TurnLine("assistant", assistant.Text));
            }

            SaveCurrent(); // 대화 기록 저장
            IsBusy = false;
        }
    }

    // 오류 원문은 사용자에게 노출하지 않고 파일로 남긴다(관리자 전달용).
    // 로그는 ASCII만 — 사용자 요청 원문(비-ASCII 가능)은 넣지 않고 길이만 기록한다.
    private static string LogError(string request, Exception ex)
    {
        MoaiCode.Config.MoaiLog.Error($"Chat request failed (request length={request.Length})", ex);
        return MoaiCode.Config.MoaiLog.FilePath;
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
        // 활성 대상 문서(편집 모드)면 대상·적용 범위를 컨텍스트로 명시한다.
        var docContext = IsDocConnected
            ? $"[작업 대상] 현재 열려 있는 {ActiveDocApp} 문서 '{ActiveDocName}' 를 COM 으로 편집합니다. " +
              $"적용 범위: {ActiveScope}. " +
              (ActiveScope == "현재 선택 영역"
                  ? "사용자가 선택한 영역/도형을 대상으로 하세요(slide_index·shape_id 를 지정하지 말고 현재 선택을 사용)."
                  : "문서 전체를 대상으로 하세요.") + "\n\n"
            : string.Empty;

        if (References.Count == 0)
        {
            return docContext.Length > 0 ? docContext + userText : userText;
        }

        var sb = new StringBuilder();
        if (docContext.Length > 0)
        {
            sb.Append(docContext);
        }

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

    // '새 작업' — 현재 대화·첨부·입력을 비우고 새 세션을 시작한다.
    [RelayCommand]
    private void NewTask()
    {
        if (IsBusy)
        {
            return; // 진행 중이면 무시(응답 도중 초기화 방지).
        }

        StartFreshSession();
        ShowHome = true; // 홈(시작) 화면으로
    }

    /// <summary>홈의 "새로운 대화" — 빈 채팅을 열어 바로 입력 가능한 상태로.</summary>
    [RelayCommand]
    private void NewConversation()
    {
        if (IsBusy)
        {
            return;
        }

        StartFreshSession();
        ShowSettings = false;
        ShowHome = false; // 채팅 화면 표시
    }

    private void StartFreshSession()
    {
        SaveCurrent();
        _sessionId = NewSessionId();
        _sessionKind = "chat";
        _sessionTargetDoc = null;
        DetachDoc(); // 활성 대상 칩 해제
        _transcript.Clear();
        Items.Clear();
        References.Clear();
        OnPropertyChanged(nameof(HasReferences));
        Input = string.Empty;
    }

}
