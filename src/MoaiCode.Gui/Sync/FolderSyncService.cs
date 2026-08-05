using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MoaiCode.Config;
using MoaiCode.Core.Tools;
using MoaiCode.Gui.Agent;
using MoaiCode.Tools.OpenXml;

namespace MoaiCode.Gui.Sync;

/// <summary>연결된 로컬 폴더 하나. Visibility/OrgId 는 하위 호환용(현재 미사용).</summary>
public sealed record ConnectedFolder(string Path, string? OrgId, string Visibility);

/// <summary>
/// 연결된 로컬 폴더를 백그라운드로 '로컬 인덱싱'한다: 로컬 청킹 + 서버 임베딩(compute-only) + 로컬 벡터
/// (.moai-chunks/*.vec + vectors.json). 원본 문서는 서버(조직 문서함)에 올라가지 않는다. 연결/시작 시
/// 1회 인덱싱하고, 파일 변경은 디바운스 후 증분 재인덱싱(미변경분은 스킵), 삭제는 로컬 벡터에서 제거한다.
/// </summary>
public sealed class FolderSyncService : IDisposable
{
    // 마지막 변경 후 이 시간(ms) 조용해야 인덱싱한다. 편집 세션 중 잦은 재인덱싱 방지.
    private const int QuietMs = 3000;

    private readonly List<ConnectedFolder> _folders = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly ConcurrentDictionary<string, DateTime> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Timer? _timer;
    private bool _disposed;

    /// <summary>트레이/UI 표시용 상태 메시지.</summary>
    public event Action<string>? Status;

    /// <summary>앱에서 만든 단일 인스턴스(VM 이 폴더 목록을 제어하기 위해 접근).</summary>
    public static FolderSyncService? Instance { get; private set; }

    public FolderSyncService() => Instance = this;

    public IReadOnlyList<ConnectedFolder> Folders => _folders;

    public int FolderCount => _folders.Count;

    public void Start(IEnumerable<ConnectedFolder> folders)
    {
        foreach (var f in folders)
        {
            Connect(f);
        }

        _timer = new Timer(_ => _ = ProcessDirtyAsync(), null, 2000, 2000);
        MoaiLog.Info($"FolderIndex: started (folders={_folders.Count})");
        Status?.Invoke(StatusText());
    }

    /// <summary>폴더를 등록하고 감시 시작 + 최초 인덱싱을 예약한다. 없는 경로는 무시.</summary>
    public void Connect(ConnectedFolder folder)
    {
        if (!Directory.Exists(folder.Path))
        {
            Status?.Invoke($"폴더 없음: {folder.Path}");
            return;
        }

        _folders.Add(folder);
        var watcher = new FileSystemWatcher(folder.Path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        watcher.Created += (_, e) => OnChanged(folder.Path, e.FullPath);
        watcher.Changed += (_, e) => OnChanged(folder.Path, e.FullPath);
        watcher.Renamed += (_, e) => OnChanged(folder.Path, e.FullPath);
        watcher.Deleted += (_, e) => OnDeleted(folder.Path, e.FullPath);
        _watchers.Add(watcher);

        _dirty[folder.Path] = DateTime.UtcNow; // 연결 즉시 최초 인덱싱 예약
        MoaiLog.Info($"FolderIndex: connected (total={_folders.Count})");
        Status?.Invoke(StatusText());
    }

    /// <summary>폴더 등록/감시 해제(로컬 인덱스 파일은 남긴다).</summary>
    public void Disconnect(string path)
    {
        var i = _folders.FindIndex(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        if (i < 0)
        {
            return;
        }

        _folders.RemoveAt(i);
        if (i < _watchers.Count)
        {
            _watchers[i].Dispose();
            _watchers.RemoveAt(i);
        }

        _dirty.TryRemove(path, out _);
        Status?.Invoke(StatusText());
    }

    // 문서형 변경만 해당 폴더를 dirty 로 표시(사이드카 .moai-chunks 쓰기로 인한 자기 트리거 제외).
    private void OnChanged(string folderPath, string changedPath)
    {
        if (IsSidecar(changedPath) || !IsIndexable(changedPath))
        {
            return;
        }

        _dirty[folderPath] = DateTime.UtcNow;
    }

    // 문서 삭제 → 로컬 벡터에서 제거(검색 대상에서 빠짐). 사이드카/비문서형은 무시.
    private void OnDeleted(string folderPath, string deletedPath)
    {
        if (IsSidecar(deletedPath) || !IsIndexable(deletedPath))
        {
            return;
        }

        try
        {
            var rel = Path.GetRelativePath(folderPath, deletedPath);
            new ChunkStore(folderPath).RemoveVectors(rel);
            MoaiLog.Info($"FolderIndex: removed deleted doc from local index ({Path.GetExtension(deletedPath).ToLowerInvariant()})");
        }
        catch (Exception ex)
        {
            MoaiLog.Warn($"FolderIndex: delete cleanup failed: {ex.GetType().Name}");
        }
    }

    private static bool IsSidecar(string path) =>
        path.Contains(Path.DirectorySeparatorChar + ChunkStore.DirName + Path.DirectorySeparatorChar)
        || path.EndsWith(Path.DirectorySeparatorChar + ChunkStore.DirName, StringComparison.Ordinal);

    private static bool IsIndexable(string path) =>
        !Path.GetFileName(path).StartsWith('.') && DocumentTextExtractor.IsSupported(path);

    private async Task ProcessDirtyAsync()
    {
        if (_disposed || !await _gate.WaitAsync(0).ConfigureAwait(false))
        {
            return; // 이전 인덱싱 진행 중 → 스킵
        }

        try
        {
            var now = DateTime.UtcNow;
            var ready = _dirty
                .Where(kv => (now - kv.Value).TotalMilliseconds > QuietMs)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var folder in ready)
            {
                _dirty.TryRemove(folder, out _);
                if (Directory.Exists(folder))
                {
                    await IndexFolderAsync(folder).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            MoaiLog.Error("FolderIndex: process cycle threw", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    // 폴더를 로컬 인덱싱한다(LocalIndexBuild 재사용, 증분). 서버 임베딩 호출 전 자격증명 확보.
    private async Task IndexFolderAsync(string path)
    {
        GuiBootstrap.EnsureEnvReady();
        Status?.Invoke($"인덱싱 중: {Path.GetFileName(path)}");
        MoaiLog.Info("FolderIndex: indexing folder start");

        var input = JsonSerializer.SerializeToElement(new { path, recursive = true });
        var ctx = new ToolContext(path, PermissionMode.Auto);

        var ok = false;
        string? last = null;
        try
        {
            await foreach (var p in new LocalIndexBuildTool().ExecuteAsync(input, ctx, CancellationToken.None).ConfigureAwait(false))
            {
                if (p is ToolOutput o)
                {
                    last = o.Text;
                    ok = !o.IsError;
                }
            }
        }
        catch (Exception ex)
        {
            MoaiLog.Warn($"FolderIndex: indexing threw: {ex.GetType().Name}");
            Status?.Invoke($"인덱싱 실패: {Path.GetFileName(path)}");
            return;
        }

        MoaiLog.Info($"FolderIndex: indexing folder done ok={ok}");
        Status?.Invoke(ok ? StatusText() : $"인덱싱 실패: {Path.GetFileName(path)}");
    }

    private string StatusText() =>
        _folders.Count == 0 ? "연결된 폴더 없음" : $"연결된 폴더 {_folders.Count}개";

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
        foreach (var w in _watchers)
        {
            w.Dispose();
        }

        _watchers.Clear();
        _folders.Clear();
        _gate.Dispose();
    }
}
