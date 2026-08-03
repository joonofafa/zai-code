using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
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

    [RelayCommand] private void ShowFoldersTab() => Tab = 0;
    [RelayCommand] private void ShowEditTab() => Tab = 1;

    // ── 공유 폴더 대시보드 ──
    public ObservableCollection<FolderRow> Folders { get; } = new();
    public int WatchedFolderCount => Folders.Count;

    [ObservableProperty] private string _syncStatus = "대기 중";

    private void InitDesktop()
    {
        LoadFolders();
        var svc = FolderSyncService.Instance;
        if (svc is not null)
        {
            svc.Status += OnSyncStatus;
            SyncStatus = svc.FolderCount == 0 ? "대기 중 (연결된 폴더 없음)" : $"{svc.FolderCount}개 폴더 감시 중";
        }
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
        }
    }
}

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
