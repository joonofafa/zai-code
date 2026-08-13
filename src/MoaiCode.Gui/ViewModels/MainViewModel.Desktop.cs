using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoaiCode.Gui.Sync;
using MoaiCode.Localization;
using MoaiCode.Tools.Office;

namespace MoaiCode.Gui.ViewModels;

// 새 동작 모델(목업 합의): 좌측 사이드바 대신 상단 2탭 —
//  탭0 "공유 폴더"(백그라운드 동기화 대시보드), 탭1 "문서 편집"(열린 Office 편집).
// 문서 생성(OpenXML)은 제거됨 — 작성·편집은 열린 Office(COM)로만.
public sealed partial class MainViewModel
{
    // 0 = 공유 폴더, 1 = 문서 편집
    [ObservableProperty] private int _tab;

    public bool IsFoldersTab => Tab == 0 && !ShowSettings;
    public bool IsEditTab => Tab == 1 && !ShowSettings;

    // 문서 편집 탭 하위 상태(ShowHome=대화 시작 전, HasOpenDocs=열린 문서 존재).
    public bool IsEditChat => IsEditTab && !ShowHome;               // 문서 선택 후 대화 편집
    public bool IsEditList => IsEditTab && ShowHome && HasOpenDocs;  // 열린 문서 목록
    public bool IsEditNone => IsEditTab && ShowHome && !HasOpenDocs; // 열려 있는 오피스 없음

    // 편집 대화인데 아직 대화가 비어 있음(템플릿 자동입력 직후 등) → "주제 입력" 안내 표시용.
    public bool IsEditChatEmpty => IsEditChat && Items.Count == 0;

    // 템플릿 프롬프트가 입력창에 채워진 직후 — 코드비하인드가 「(주제)」 를 선택하도록.
    public event Action? TemplateFilled;

    [RelayCommand] private void ShowFoldersTab() => Tab = 0;
    [RelayCommand] private void ShowEditTab() => Tab = 1;

    // 편집 대화 → 열린 문서 목록으로 복귀("← 목록"). 세션/문서 연결은 유지.
    [RelayCommand] private void BackToList() => ShowHome = true;

    // ── 새 문서 템플릿(프롬프트 프리셋) — 카드 클릭 시 입력창에 프롬프트 자동 입력 ──
    public ObservableCollection<DocTemplate> WordTemplates { get; } = new();
    public ObservableCollection<DocTemplate> PptTemplates { get; } = new();
    public ObservableCollection<DocTemplate> ExcelTemplates { get; } = new();

    private static IBrush Sw(string hex) => new SolidColorBrush(Color.Parse(hex));

    private void BuildTemplates()
    {
        if (WordTemplates.Count > 0)
        {
            return;
        }

        var blank = Sw("#8A93A3");
        var w = Sw("#2B579A");
        var x = Sw("#217346");
        var topic = L10n.Get("gui.topic.sentinel");

        WordTemplates.Add(new("Word", L10n.Get("gui.tpl.blankDoc"), null, blank, true));
        WordTemplates.Add(new("Word", L10n.Get("gui.tpl.report"),
            L10n.Get("gui.prompt.docReport", topic), w, false));
        WordTemplates.Add(new("Word", L10n.Get("gui.tpl.incident"),
            L10n.Get("gui.prompt.docIncident", topic), w, false));
        WordTemplates.Add(new("Word", L10n.Get("gui.tpl.proposal"),
            L10n.Get("gui.prompt.docProposal", topic), w, false));

        PptTemplates.Add(new("PowerPoint", L10n.Get("gui.tpl.blankPpt"), null, blank, true));
        PptTemplates.Add(new("PowerPoint", L10n.Get("gui.tpl.designA"),
            L10n.Get("gui.prompt.pptA", topic), Sw("#2F5496"), false));
        PptTemplates.Add(new("PowerPoint", L10n.Get("gui.tpl.designB"),
            L10n.Get("gui.prompt.pptB", topic), Sw("#C00000"), false));
        PptTemplates.Add(new("PowerPoint", L10n.Get("gui.tpl.designC"),
            L10n.Get("gui.prompt.pptC", topic), Sw("#222222"), false));
        PptTemplates.Add(new("PowerPoint", L10n.Get("gui.tpl.designD"),
            L10n.Get("gui.prompt.pptD", topic), Sw("#4FC3F7"), false));

        ExcelTemplates.Add(new("Excel", L10n.Get("gui.tpl.blankXls"), null, blank, true));
        ExcelTemplates.Add(new("Excel", L10n.Get("gui.tpl.expense"),
            L10n.Get("gui.prompt.xlsExpense"), x, false));
        ExcelTemplates.Add(new("Excel", L10n.Get("gui.tpl.invoice"),
            L10n.Get("gui.prompt.xlsInvoice"), x, false));
        ExcelTemplates.Add(new("Excel", L10n.Get("gui.tpl.inventory"),
            L10n.Get("gui.prompt.xlsInventory"), x, false));
    }

    // ── 주제 입력 팝업(「(주제)」 가 있는 템플릿) — 자료 출처 토글 포함 ──
    [ObservableProperty] private bool _showTopicDialog;
    [ObservableProperty] private string _topicInput = string.Empty;
    [ObservableProperty] private string _topicTemplateLabel = string.Empty;

    // 주제 팝업 제목("{라벨} · 주제를 입력하세요") — 로컬라이즈된 서식. 라벨 변경 시 갱신.
    public string TopicDialogTitle => MoaiCode.Localization.L10n.Get("gui.topic.titleFmt", TopicTemplateLabel);

    partial void OnTopicTemplateLabelChanged(string value) => OnPropertyChanged(nameof(TopicDialogTitle));
    [ObservableProperty] private bool _srcWeb;
    [ObservableProperty] private bool _srcOrg;
    // 연결된 로컬 폴더 각각을 참고 대상 토글로. 작성 시 선택된 폴더를 LocalDocsSearch 로 검색.
    public ObservableCollection<FolderChoice> TopicFolders { get; } = new();
    private DocTemplate? _pendingTemplateForTopic;

    // 템플릿 카드 클릭 — 빈 문서는 앱 바로 열기, 주제형은 팝업, 양식형(주제 불필요)은 바로 진행.
    [RelayCommand]
    private void UseTemplate(DocTemplate? t)
    {
        if (t is null || IsBusy)
        {
            return;
        }

        if (t.IsBlank || string.IsNullOrEmpty(t.Prompt))
        {
            Tab = 1;
            LaunchApp(t.App);
            return;
        }

        // 「(주제)」 가 있으면 주제 입력 팝업을 먼저(사용자가 놓치지 않도록). 자료 출처 토글도 초기화.
        if (t.Prompt.Contains(L10n.Get("gui.topic.sentinel"), StringComparison.Ordinal))
        {
            _pendingTemplateForTopic = t;
            TopicTemplateLabel = t.Label;
            TopicInput = string.Empty;
            SrcWeb = false;
            SrcOrg = false;
            TopicFolders.Clear();
            foreach (var f in Folders)
            {
                TopicFolders.Add(new FolderChoice(f.Path));
            }

            ShowTopicDialog = true;
            return;
        }

        StartTemplate(t, t.Prompt); // 양식형(Excel 등) — 주제 없이 바로
    }

    [RelayCommand]
    private void ConfirmTopic()
    {
        var t = _pendingTemplateForTopic;
        if (t is null)
        {
            return;
        }

        var topic = string.IsNullOrWhiteSpace(TopicInput) ? null : TopicInput.Trim();
        var sentinel = L10n.Get("gui.topic.sentinel");
        var prompt = topic is null
            ? t.Prompt!.Replace(sentinel, string.Empty).Trim()
            : t.Prompt!.Replace(sentinel, $"「{topic}」");

        // 선택된 자료 출처를 프롬프트에 결정적으로 결합(모델이 반드시 해당 소스로 근거 수집).
        var sources = new List<string>();
        if (SrcWeb)
        {
            sources.Add(L10n.Get("gui.prompt.srcWeb"));
        }

        if (SrcOrg)
        {
            sources.Add(L10n.Get("gui.prompt.srcOrg"));
        }

        foreach (var fc in TopicFolders.Where(f => f.Selected))
        {
            sources.Add(L10n.Get("gui.prompt.srcLocalFmt", fc.Path));
        }

        if (sources.Count > 0)
        {
            prompt += "\n\n" + L10n.Get("gui.prompt.srcHeader") + "\n- "
                      + string.Join("\n- ", sources);
        }

        ShowTopicDialog = false;
        _pendingTemplateForTopic = null;
        StartTemplate(t, prompt);
    }

    [RelayCommand]
    private void CancelTopic()
    {
        ShowTopicDialog = false;
        _pendingTemplateForTopic = null;
    }

    // 프롬프트(자료 출처 포함)를 새 대화로 시작하고 바로 전송.
    private void StartTemplate(DocTemplate t, string prompt)
    {
        Tab = 1;
        StartFreshSession();  // 새 문서 작업은 별도 대화로
        Input = prompt;
        ActiveDocApp = t.App;  // 하단 컴포저를 대상 앱 색으로 강조
        ShowHome = false;
        SendCommand.Execute(null);
    }

    // ── 공유 폴더 대시보드 ──
    public ObservableCollection<FolderRow> Folders { get; } = new();
    public int WatchedFolderCount => Folders.Count;

    [ObservableProperty] private string _syncStatus = L10n.Get("gui.status.idle");

    private Timer? _aliveTimer;
    private bool _aliveChecking; // COM 열거 재진입 방지(모달 Office 로 타임아웃이 쌓이는 것을 막음)

    private void InitDesktop()
    {
        LoadFolders();
        BuildTemplates();
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEditChatEmpty));
        var svc = FolderSyncService.Instance;
        if (svc is not null)
        {
            svc.Status += OnSyncStatus;
            SyncStatus = svc.FolderCount == 0 ? L10n.Get("gui.status.noFolders") : L10n.Get("gui.status.foldersFmt", svc.FolderCount);
        }

        // 연결된 문서가 사용자에 의해 닫혔는지 주기 확인(연결 중일 때만 COM 열거). 창 포커스 복귀 시에도 확인.
        _aliveTimer = new Timer(
            _ => Dispatcher.UIThread.Post(CheckActiveDocAlive),
            null, 5000, 5000);
    }

    /// <summary>연결된 Office 문서가 아직 열려 있는지 확인. 닫혔으면 연결 해제 + 목록으로 복귀.
    /// COM 열거는 UI 를 막지 않도록 비동기(타임아웃). 재진입은 무시한다.</summary>
    public async void CheckActiveDocAlive()
    {
        if (_aliveChecking || !IsDocConnected || string.IsNullOrEmpty(_sessionTargetDoc))
        {
            return;
        }

        System.Collections.Generic.IReadOnlyList<OfficeDoc>? open;
        _aliveChecking = true;
        try
        {
            open = await OfficeWindowLister.ListOpenDocumentsAsync();
        }
        catch
        {
            return; // 열거 실패 시 상태 유지(오탐 방지)
        }
        finally
        {
            _aliveChecking = false;
        }

        // 열거 타임아웃(null)은 "닫힘"이 아니다 — 모달/busy 일 뿐이므로 연결 유지. 연결 상태가
        // 그새 바뀌었으면(사용자가 해제 등)도 무시.
        if (open is null || !IsDocConnected || string.IsNullOrEmpty(_sessionTargetDoc))
        {
            return;
        }

        var alive = open.Any(d =>
            (d.Path is not null && _sessionDocPath is not null
                && string.Equals(d.Path, _sessionDocPath, StringComparison.OrdinalIgnoreCase))
            || string.Equals(d.Name, _sessionTargetDoc, StringComparison.Ordinal));
        if (alive)
        {
            return;
        }

        // 연결 문서가 닫힘 → 해제하고 열린 문서 목록으로.
        DetachDoc();
        OfficeDocs.Clear();
        foreach (var d in open)
        {
            OfficeDocs.Add(d);
        }

        OnPropertyChanged(nameof(HasOpenDocs));
        ShowHome = true; // 편집 탭: 목록 / 없음 상태로 전환
    }

    private void OnSyncStatus(string msg) => Dispatcher.UIThread.Post(() => SyncStatus = msg);

    private void LoadFolders()
    {
        Folders.Clear();
        foreach (var f in GuiSettings.Load().ConnectedFolders)
        {
            Folders.Add(new FolderRow(f.Path, f.OrgId, f.Visibility));
        }

        OnPropertyChanged(nameof(WatchedFolderCount));
    }

    /// <summary>로컬 폴더 연결(코드비하인드에서 폴더 선택 후 호출). 설정에 등록만 한다(자동 업로드 없음).</summary>
    public void AddFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var s = GuiSettings.Load();
        if (s.ConnectedFolders.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var folder = new ConnectedFolder(path, null, "local");
        s.ConnectedFolders.Add(folder);
        s.Save();
        FolderSyncService.Instance?.Connect(folder);
        LoadFolders();
    }

    [RelayCommand]
    private void UnlinkFolder(FolderRow? row)
    {
        if (row is null)
        {
            return;
        }

        var s = GuiSettings.Load();
        s.ConnectedFolders.RemoveAll(f => string.Equals(f.Path, row.Path, StringComparison.OrdinalIgnoreCase));
        s.Save();
        FolderSyncService.Instance?.Disconnect(row.Path);
        LoadFolders();
    }

    // 연결된 폴더를 파일 탐색기로 연다.
    [RelayCommand]
    private void OpenFolder(FolderRow? row)
    {
        if (row is null || string.IsNullOrWhiteSpace(row.Path))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(row.Path) { UseShellExecute = true });
        }
        catch
        {
            // 폴더 열기 실패는 치명적 아님.
        }
    }

    // ── 앱 열기(열린 문서 없을 때) — 실행 후 편집 탭으로, 잠시 뒤 목록 새로고침 ──
    [RelayCommand] private void OpenWord() => LaunchApp("Word");

    [RelayCommand] private void OpenExcel() => LaunchApp("Excel");

    [RelayCommand] private void OpenPowerPoint() => LaunchApp("PowerPoint");

    private Timer? _launchTimer;

    private void LaunchApp(string app)
    {
        try
        {
            OfficeWindowLister.Launch(app);
        }
        catch
        {
            // 실행 실패는 무시(설치 안 됨 등) — 사용자는 목록으로 확인.
        }

        Tab = 1;
        // 앱 기동에 시간이 걸리므로 1.5초 뒤 열린 문서 목록 갱신.
        _launchTimer?.Dispose();
        _launchTimer = new Timer(
            _ => Dispatcher.UIThread.Post(() => RefreshOfficeCommand.Execute(null)),
            null, 1500, Timeout.Infinite);
    }

    // 파생(탭·하위상태) 프로퍼티는 Tab/ShowHome/ShowSettings/HasOpenDocs 변화 시 함께 갱신.
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Tab) or nameof(ShowHome) or nameof(ShowSettings) or nameof(HasOpenDocs))
        {
            OnPropertyChanged(nameof(IsFoldersTab));
            OnPropertyChanged(nameof(IsEditTab));
            OnPropertyChanged(nameof(IsEditChat));
            OnPropertyChanged(nameof(IsEditList));
            OnPropertyChanged(nameof(IsEditNone));
            OnPropertyChanged(nameof(IsEditChatEmpty));
        }
    }
}

/// <summary>새 문서 템플릿 카드(프롬프트 프리셋). Prompt 가 null 이면 빈 문서(앱만 열기).</summary>
public sealed record DocTemplate(string App, string Label, string? Prompt, IBrush Swatch, bool IsBlank);

/// <summary>주제 팝업의 '연결 폴더 참고' 토글 항목(체크박스 바인딩용 observable).</summary>
public partial class FolderChoice : ObservableObject
{
    public FolderChoice(string path)
    {
        Path = path;
        var name = System.IO.Path.GetFileName(path.TrimEnd('/', '\\'));
        Label = L10n.Get("gui.folders.localPrefixFmt", string.IsNullOrEmpty(name) ? path : name);
    }

    public string Path { get; }

    public string Label { get; }

    [ObservableProperty] private bool _selected;
}

/// <summary>공유 폴더 대시보드 행(표시용).</summary>
public sealed record FolderRow(string Path, string? OrgId, string Visibility)
{
    public string Display => Path;

    public string VisibilityLabel => Visibility switch
    {
        "organization" => L10n.Get("gui.vis.organization"),
        "private" => L10n.Get("gui.vis.private"),
        "company" => L10n.Get("gui.vis.company"),
        "org" => L10n.Get("gui.vis.orgLegacy"),   // 구버전 저장값 호환
        "team" => L10n.Get("gui.vis.organization"),  // 구버전 저장값 호환(→ organization)
        _ => Visibility,
    };

    public string OrgLabel => string.IsNullOrWhiteSpace(OrgId) ? L10n.Get("gui.folders.defaultBox") : OrgId!;
}
