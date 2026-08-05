using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MoaiCode.Config;
using MoaiCode.Core.Tools;
using MoaiCode.Gui.Agent;
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
        MoaiLog.Info($"FolderSync: started (folders={_folders.Count})");
        Status?.Invoke(_folders.Count == 0 ? "대기 중 (연결된 폴더 없음)" : $"{_folders.Count}개 폴더 감시 중");
    }

    public void Connect(ConnectedFolder folder)
    {
        if (!Directory.Exists(folder.Path))
        {
            MoaiLog.Warn($"FolderSync: connect skipped, folder missing (len={folder.Path?.Length ?? 0})");
            Status?.Invoke($"폴더 없음: {folder.Path}");
            return;
        }

        _folders.Add(folder);
        MoaiLog.Info($"FolderSync: connected folder (total={_folders.Count}, vis={MapVisibility(folder.Visibility)}, orgId={(string.IsNullOrEmpty(folder.OrgId) ? "auto" : "set")})");
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

        // 초기 스캔: 연결 시점에 이미 폴더에 있던 문서들도 한 번 큐잉한다. 워처는 '앞으로의 변경'만
        // 잡으므로 이게 없으면 기존 파일은 영영 안 올라간다. 이미 올린 미변경분은 매니페스트가 스킵.
        ScanExisting(folder);

        // 런타임 추가 시에도 상단 상태 pill 이 갱신되도록 알린다(Start 경로는 뒤에서 한 번 더 덮어씀).
        Status?.Invoke($"{_folders.Count}개 폴더 감시 중");
    }

    // 연결 폴더의 기존 문서형 파일을 초기 큐잉한다(하위 포함). 실제 업로드는 처리 루프에서 매니페스트
    // 검사 후 수행되므로, 이미 올린 미변경 파일은 여기서 큐잉돼도 스킵된다.
    private void ScanExisting(ConnectedFolder folder)
    {
        try
        {
            var n = 0;
            foreach (var f in Directory.EnumerateFiles(folder.Path, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(f);
                if (name.StartsWith('.') || !DocumentTextExtractor.IsSupported(f))
                {
                    continue;
                }

                _pending[f] = (DateTime.UtcNow, folder);
                n++;
            }

            MoaiLog.Info($"FolderSync: initial scan queued {n} existing file(s)");
        }
        catch (Exception ex)
        {
            MoaiLog.Warn($"FolderSync: initial scan failed: {ex.GetType().Name}");
        }
    }

    private void Enqueue(ConnectedFolder folder, string path)
    {
        // 문서형만(지식베이스 임베딩 대상). 숨김/사이드카 제외.
        var name = Path.GetFileName(path);
        if (name.StartsWith('.') || !DocumentTextExtractor.IsSupported(path))
        {
            MoaiLog.Debug($"FolderSync: event ignored {Tag(path)} (hidden or unsupported type)");
            return;
        }

        _pending[path] = (DateTime.UtcNow, folder);
        MoaiLog.Debug($"FolderSync: event queued {Tag(path)} (pending={_pending.Count})");
    }

    // 로그용 ASCII 식별자 — 원문(한글 가능) 파일명 대신 확장자 + 경로 안정 해시. CLAUDE.md 로깅 규칙 준수.
    private static string Tag(string path)
    {
        uint h = 2166136261;
        foreach (var c in path)
        {
            h ^= c;
            h *= 16777619;
        }

        return $"{Path.GetExtension(path).ToLowerInvariant()} #{h & 0xffffff:x6}";
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

            if (ready.Count > 0)
            {
                MoaiLog.Debug($"FolderSync: cycle ready={ready.Count} pending={_pending.Count}");
            }

            foreach (var path in ready)
            {
                _pending.TryRemove(path, out var entry);
                if (!File.Exists(path))
                {
                    MoaiLog.Debug($"FolderSync: skip {Tag(path)} (file gone)");
                    continue;
                }

                if (_manifest.IsUnchanged(path))
                {
                    MoaiLog.Debug($"FolderSync: skip {Tag(path)} (unchanged since last upload)");
                    continue;
                }

                await UploadAsync(path, entry.Folder).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            MoaiLog.Error("FolderSync: process cycle threw", ex);
            Status?.Invoke($"동기화 오류: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    // UI 의 공개범위 값(team/org/private)을 서버 문서함 enum(private|organization|company)으로 매핑한다.
    // 서버는 이 세 값만 인정하므로 미매핑 값은 private 로 안전하게 떨어뜨린다. 계층: 팀 ⊂ 조직.
    private static string MapVisibility(string? ui) => ui?.Trim().ToLowerInvariant() switch
    {
        "team" => "organization",       // 팀 공개 → 조직 범위
        "org" => "company",             // 조직 공개 → 회사 전체
        "organization" => "organization",
        "company" => "company",
        _ => "private",                 // 비공개/미지정
    };

    private async Task UploadAsync(string path, ConnectedFolder folder)
    {
        var name = Path.GetFileName(path);
        Status?.Invoke($"올리는 중: {name}");

        // 백그라운드 동기화는 채팅 엔진 빌드 전에 돌 수 있어, 여기서 자격증명/서버주소 환경변수를 스스로 확보한다.
        GuiBootstrap.EnsureEnvReady();
        var baseSet = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_BASE_URL"));
        var keySet = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
        var vis = MapVisibility(folder.Visibility);
        long size = 0;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch
        {
            // size only for logging
        }

        MoaiLog.Info($"FolderSync: upload start {Tag(path)} size={size} baseUrl={(baseSet ? "set" : "MISSING")} key={(keySet ? "set" : "MISSING")} vis={vis}");
        if (!baseSet || !keySet)
        {
            MoaiLog.Warn("FolderSync: upload aborted, credentials/baseUrl missing (login required or settings.json baseUrl unset)");
        }

        var input = JsonSerializer.SerializeToElement(new
        {
            orgId = folder.OrgId,
            path,
            visibility = vis,
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
            MoaiLog.Info($"FolderSync: upload ok {Tag(path)} docId={docId ?? "?"}");
            Status?.Invoke($"문서함 반영됨: {name}");
        }
        else
        {
            MoaiLog.Warn($"FolderSync: upload failed {Tag(path)} reason={ClassifyFailure(last)}");
            Status?.Invoke($"실패: {name} — {last}");
        }
    }

    // 업로드 실패 메시지(한글 가능)를 로그용 ASCII 사유 코드로 분류한다(원문은 로그에 넣지 않음).
    private static string ClassifyFailure(string? msg) => msg switch
    {
        null => "no_output",
        _ when msg.Contains("연결 정보가 없습니다") => "no_credentials",
        _ when msg.Contains("엔드포인트") => "endpoint_missing",
        _ when msg.Contains("타임아웃") => "timeout",
        _ when msg.Contains("요청 실패") => "request_failed",
        _ when msg.Contains("거부") => "rejected_by_server",
        _ when msg.Contains("찾지 못했습니다") => "no_documents_resolved",
        _ => "other",
    };

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
