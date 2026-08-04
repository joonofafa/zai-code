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

        WordTemplates.Add(new("Word", "빈 문서", null, blank, true));
        WordTemplates.Add(new("Word", "보고서",
            "Word에 「(주제)」 주제로 보고서를 작성해줘. 새 Word 문서로 시작하고, 개요·배경·현황·분석·결론 및 제언 섹션 구조로, 핵심 내용은 실제 표로 정리하고 글자 크기는 본문에 맞춰줘.", w, false));
        WordTemplates.Add(new("Word", "경위서",
            "Word에 「(주제)」 경위서를 작성해줘. 새 Word 문서로 시작하고, 발생 개요·경위·원인·조치 사항·재발 방지 대책 섹션으로.", w, false));
        WordTemplates.Add(new("Word", "제안서",
            "Word에 「(주제)」 제안서를 작성해줘. 새 Word 문서로 시작하고, 배경 및 목적·제안 내용·기대 효과·추진 일정·소요 예산 섹션으로, 일정·예산은 표로.", w, false));

        PptTemplates.Add(new("PowerPoint", "빈 프레젠테이션", null, blank, true));
        PptTemplates.Add(new("PowerPoint", "디자인 A",
            "PowerPoint에 「(주제)」 발표자료를 만들어줘. 새 프레젠테이션으로 시작하고, 코퍼레이트 톤(연그레이 배경·남색 포인트), 제목 크게·본문 간결한 불릿, 표지 + 핵심 슬라이드로.", Sw("#2F5496"), false));
        PptTemplates.Add(new("PowerPoint", "디자인 B",
            "PowerPoint에 「(주제)」 발표자료를 만들어줘. 새 프레젠테이션으로 시작하고, 키노트 톤(흰 배경·빨강 포인트), 한 슬라이드 한 메시지, 큰 제목.", Sw("#C00000"), false));
        PptTemplates.Add(new("PowerPoint", "디자인 C",
            "PowerPoint에 「(주제)」 발표자료를 만들어줘. 새 프레젠테이션으로 시작하고, 미니멀 톤(흰 배경·검정, 여백 넉넉), 간결하게.", Sw("#222222"), false));
        PptTemplates.Add(new("PowerPoint", "디자인 D",
            "PowerPoint에 「(주제)」 발표자료를 만들어줘. 새 프레젠테이션으로 시작하고, 다크 톤(짙은 배경·시안 포인트), 임팩트 있게.", Sw("#4FC3F7"), false));

        ExcelTemplates.Add(new("Excel", "빈 통합문서", null, blank, true));
        ExcelTemplates.Add(new("Excel", "지출결의서",
            "Excel에 지출결의서 양식을 만들어줘. 새 Excel 통합문서로 시작하고, 일자·적요·금액·비고 표에 합계 행, 금액은 원화 서식으로.", x, false));
        ExcelTemplates.Add(new("Excel", "거래명세서",
            "Excel에 거래명세서 양식을 만들어줘. 새 Excel 통합문서로 시작하고, 품목·규격·수량·단가·금액 표에 합계 행.", x, false));
        ExcelTemplates.Add(new("Excel", "재고관리표",
            "Excel에 재고관리표 양식을 만들어줘. 새 Excel 통합문서로 시작하고, 품목·규격·입고·출고·재고·비고 표.", x, false));
    }

    // 템플릿 카드 클릭 — 빈 문서는 앱 바로 열기, 그 외엔 새 대화로 분리 + 입력창에 프롬프트 자동 입력.
    [RelayCommand]
    private void UseTemplate(DocTemplate? t)
    {
        if (t is null || IsBusy)
        {
            return;
        }

        Tab = 1;
        if (t.IsBlank || string.IsNullOrEmpty(t.Prompt))
        {
            LaunchApp(t.App);
            return;
        }

        StartFreshSession(); // 새 문서 작업은 별도 대화로(기존 문서 연결·대화 이력과 섞이지 않게)
        Input = t.Prompt;    // 입력창 자동 채움(사용자가 주제 넣고 전송). StartFreshSession 뒤에 세팅
        ActiveDocApp = t.App; // 하단 컴포저를 대상 앱 색으로 강조(문서 연결 전 힌트)
        ShowHome = false;    // 편집 대화 화면으로 전환
        TemplateFilled?.Invoke(); // 코드비하인드가 「(주제)」 선택
    }

    // ── 공유 폴더 대시보드 ──
    public ObservableCollection<FolderRow> Folders { get; } = new();
    public int WatchedFolderCount => Folders.Count;

    [ObservableProperty] private string _syncStatus = "대기 중";

    private Timer? _aliveTimer;

    private void InitDesktop()
    {
        LoadFolders();
        BuildTemplates();
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEditChatEmpty));
        var svc = FolderSyncService.Instance;
        if (svc is not null)
        {
            svc.Status += OnSyncStatus;
            SyncStatus = svc.FolderCount == 0 ? "대기 중 (연결된 폴더 없음)" : $"{svc.FolderCount}개 폴더 감시 중";
        }

        // 연결된 문서가 사용자에 의해 닫혔는지 주기 확인(연결 중일 때만 COM 열거). 창 포커스 복귀 시에도 확인.
        _aliveTimer = new Timer(
            _ => Dispatcher.UIThread.Post(CheckActiveDocAlive),
            null, 5000, 5000);
    }

    /// <summary>연결된 Office 문서가 아직 열려 있는지 확인. 닫혔으면 연결 해제 + 목록으로 복귀.</summary>
    public void CheckActiveDocAlive()
    {
        if (!IsDocConnected || string.IsNullOrEmpty(_sessionTargetDoc))
        {
            return;
        }

        System.Collections.Generic.IReadOnlyList<OfficeDoc> open;
        try
        {
            open = OfficeWindowLister.ListOpenDocuments();
        }
        catch
        {
            return; // 열거 실패 시 상태 유지(오탐 방지)
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

    /// <summary>공유 폴더 연결(코드비하인드에서 폴더 선택 후 호출). 설정 저장 + 즉시 감시 시작.</summary>
    public void AddFolder(string path, string? orgId = null, string visibility = "team")
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

        var folder = new ConnectedFolder(path, orgId, visibility);
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

/// <summary>공유 폴더 대시보드 행(표시용).</summary>
public sealed record FolderRow(string Path, string? OrgId, string Visibility)
{
    public string Display => Path;

    public string VisibilityLabel => Visibility switch
    {
        "org" => "조직 공개",
        "team" => "팀 공개",
        "private" => "비공개",
        _ => Visibility,
    };

    public string OrgLabel => string.IsNullOrWhiteSpace(OrgId) ? "기본 문서함" : OrgId!;
}
