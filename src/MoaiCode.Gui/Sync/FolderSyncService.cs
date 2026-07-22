using System;
using System.Collections.Generic;
using System.IO;

namespace MoaiCode.Gui.Sync;

/// <summary>연결된 공유 폴더 하나(→ 조직 문서함 매핑).</summary>
public sealed record ConnectedFolder(string Path, string OrgId, string Visibility);

/// <summary>
/// 공유 폴더를 감시해 변경 문서를 조직 문서함에 반영하는 백그라운드 서비스(뼈대).
/// 실제 업로드(OrgDocsUpload)·디바운스·중복 매니페스트는 다음 단계에 채운다. 지금은 구조만.
/// </summary>
public sealed class FolderSyncService : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly List<ConnectedFolder> _folders = new();

    /// <summary>트레이/UI 표시용 상태 메시지.</summary>
    public event Action<string>? Status;

    public IReadOnlyList<ConnectedFolder> Folders => _folders;

    /// <summary>저장된 연결 폴더를 로드하고 감시를 시작한다(뼈대: 아직 감시 비활성).</summary>
    public void Start()
    {
        // TODO: 설정에서 연결 폴더 목록 로드 → Connect 호출.
        Status?.Invoke("동기화 대기 중 (연결된 폴더 없음)");
    }

    public void Connect(ConnectedFolder folder)
    {
        _folders.Add(folder);
        Watch(folder);
        Status?.Invoke($"폴더 연결됨: {folder.Path}");
    }

    private void Watch(ConnectedFolder folder)
    {
        if (!Directory.Exists(folder.Path))
        {
            return;
        }

        var watcher = new FileSystemWatcher(folder.Path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = false, // 뼈대 — 아직 비활성(실사용 시 true + 디바운스)
        };
        watcher.Created += (_, e) => OnChanged(folder, e.FullPath);
        watcher.Changed += (_, e) => OnChanged(folder, e.FullPath);
        _watchers.Add(watcher);
    }

    private void OnChanged(ConnectedFolder folder, string fullPath)
    {
        // TODO: 문서형 필터 → 디바운스 → 업로드 매니페스트로 중복/미변경 스킵
        //       → OrgDocsUpload(folder.OrgId, folder.Visibility) → 트레이 알림.
        Status?.Invoke($"변경 감지: {Path.GetFileName(fullPath)} (업로드 예정)");
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }

        _watchers.Clear();
    }
}
