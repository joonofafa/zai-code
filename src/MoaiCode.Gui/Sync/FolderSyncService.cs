using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MoaiCode.Core.Tools;
using MoaiCode.Tools.Knowledge;
using MoaiCode.Tools.OpenXml;

namespace MoaiCode.Gui.Sync;

/// <summary>연결된 공유 폴더 하나(→ 조직 문서함 매핑). orgId 비우면 서버가 로그인으로 자동 해소.</summary>
public sealed record ConnectedFolder(string Path, string? OrgId, string Visibility);

/// <summary>
/// 공유 폴더를 감시해 변경 문서를 조직 문서함에 반영하는 백그라운드 서비스.
/// 감시 → 디바운스 → 문서형 필터 → 매니페스트로 미변경 스킵 → OrgDocsUpload(서버가 임베딩) → 상태 알림.
/// </summary>
public sealed class FolderSyncService : IDisposable
{
    private static readonly Regex IdRx = new(@"id=(\d+)", RegexOptions.Compiled);

    private readonly SyncManifest _manifest = SyncManifest.Load();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly List<ConnectedFolder> _folders = new();
    private readonly ConcurrentDictionary<string, (DateTime When, ConnectedFolder Folder)> _pending = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Timer? _timer;
    private bool _disposed;

    /// <summary>트레이/UI 표시용 상태 메시지.</summary>
    public event Action<string>? Status;

    /// <summary>앱에서 만든 단일 인스턴스(VM 이 폴더 대시보드를 제어하기 위해 접근).</summary>
    public static FolderSyncService? Instance { get; private set; }

    public FolderSyncService() => Instance = this;

    public IReadOnlyList<ConnectedFolder> Folders => _folders;

    public int FolderCount => _folders.Count;

    /// <summary>연결 폴더 하나를 감시 해제(파일 워처 중단). _folders/_watchers 는 추가 순서가 일치.</summary>
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

        Status?.Invoke(_folders.Count == 0 ? "대기 중 (연결된 폴더 없음)" : $"{_folders.Count}개 폴더 감시 중");
    }

    public void Start(IEnumerable<ConnectedFolder> folders)
    {
        foreach (var f in folders)
        {
            Connect(f);
        }

        // 디바운스 처리 루프(1초마다, 마지막 이벤트 후 조용해진 파일만 업로드).
        _timer = new Timer(_ => _ = ProcessPendingAsync(), null, 1000, 1000);
        Status?.Invoke(_folders.Count == 0 ? "대기 중 (연결된 폴더 없음)" : $"{_folders.Count}개 폴더 감시 중");
    }

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
        watcher.Created += (_, e) => Enqueue(folder, e.FullPath);
        watcher.Changed += (_, e) => Enqueue(folder, e.FullPath);
        watcher.Renamed += (_, e) => Enqueue(folder, e.FullPath);
        _watchers.Add(watcher);
    }

    private void Enqueue(ConnectedFolder folder, string path)
    {
        // 문서형만(지식베이스 임베딩 대상). 숨김/사이드카 제외.
        var name = Path.GetFileName(path);
        if (name.StartsWith('.') || !DocumentTextExtractor.IsSupported(path))
        {
            return;
        }

        _pending[path] = (DateTime.UtcNow, folder);
    }

    private async Task ProcessPendingAsync()
    {
        if (_disposed || !await _gate.WaitAsync(0).ConfigureAwait(false))
        {
            return; // 이전 사이클 진행 중 → 스킵
        }

        try
        {
            var now = DateTime.UtcNow;
            var ready = _pending
                .Where(kv => (now - kv.Value.When).TotalMilliseconds > 1500)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var path in ready)
            {
                _pending.TryRemove(path, out var entry);
                if (!File.Exists(path) || _manifest.IsUnchanged(path))
                {
                    continue; // 삭제됐거나 미변경 → 스킵
                }

                await UploadAsync(path, entry.Folder).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Status?.Invoke($"동기화 오류: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task UploadAsync(string path, ConnectedFolder folder)
    {
        var name = Path.GetFileName(path);
        Status?.Invoke($"올리는 중: {name}");

        var input = JsonSerializer.SerializeToElement(new
        {
            orgId = folder.OrgId,
            path,
            visibility = folder.Visibility,
        });
        var ctx = new ToolContext(Path.GetDirectoryName(path) ?? path, PermissionMode.Auto);

        var ok = false;
        string? last = null;
        await foreach (var p in new OrgDocsUploadTool().ExecuteAsync(input, ctx, CancellationToken.None).ConfigureAwait(false))
        {
            if (p is ToolOutput o)
            {
                last = o.Text;
                ok = !o.IsError;
            }
        }

        if (ok)
        {
            var docId = last is not null && IdRx.Match(last) is { Success: true } m ? m.Groups[1].Value : null;
            _manifest.MarkUploaded(path, docId);
            _manifest.Save();
            Status?.Invoke($"문서함 반영됨: {name}");
        }
        else
        {
            Status?.Invoke($"실패: {name} — {last}");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
        foreach (var w in _watchers)
        {
            w.Dispose();
        }

        _watchers.Clear();
        _gate.Dispose();
    }
}
