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
using MoaiCode.Tools.Office;
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

    // 마지막 변경 후 이 시간(ms) 조용해야 업로드 후보. 편집 세션 중 잦은 재업로드를 줄이는 디바운스.
    private const int QuietMs = 3000;

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

    // Office 로 편집되는(=열림 판정 대상) 문서형인가. txt/md/csv/pdf 는 Office 열림 판정 없이 디바운스만.
    private static bool IsOfficeDoc(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".docx" or ".xlsx" or ".pptx";

    // 현재 Office(Word/Excel/PowerPoint)에 열려 있는 문서의 전체경로 집합. 편집 중 문서 업로드 보류에 쓴다.
    // 비-Windows 는 빈 집합(열린 것 없음), COM 열거 실패/타임아웃은 null(판단 불가).
    private static async Task<HashSet<string>?> GetOpenOfficePathsAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var docs = await OfficeWindowLister.ListOpenDocumentsAsync().ConfigureAwait(false);
        if (docs is null)
        {
            return null;
        }

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in docs)
        {
            if (!string.IsNullOrEmpty(d.Path))
            {
                set.Add(d.Path);
            }
        }

        return set;
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
                .Where(kv => (now - kv.Value.When).TotalMilliseconds > QuietMs)
                .Select(kv => kv.Key)
                .ToList();

            if (ready.Count == 0)
            {
                return;
            }

            MoaiLog.Debug($"FolderSync: cycle ready={ready.Count} pending={_pending.Count}");

            // '닫힘 시 동기화': 편집 중(Office 에 열려 있는) 문서는 업로드를 미룬다 — 저장할 때마다
            // 재업로드/재임베딩하는 낭비를 막고, 사용자가 문서를 닫은 뒤 한 번만 올린다. Office 목록을
            // 못 읽으면(null) 판단 불가 → 디바운스만으로 진행(중복은 아래 교체 로직이 흡수).
            // 대기열에 Office 문서가 하나도 없으면 COM 열거 자체를 생략(txt/md 등 편집 시 낭비 방지).
            var anyOfficeDoc = ready.Exists(IsOfficeDoc);
            var openInOffice = anyOfficeDoc
                ? await GetOpenOfficePathsAsync().ConfigureAwait(false)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in ready)
            {
                if (!File.Exists(path))
                {
                    _pending.TryRemove(path, out _);
                    MoaiLog.Debug($"FolderSync: skip {Tag(path)} (file gone)");
                    continue;
                }

                if (_manifest.IsUnchanged(path))
                {
                    _pending.TryRemove(path, out _);
                    MoaiLog.Debug($"FolderSync: skip {Tag(path)} (unchanged since last upload)");
                    continue;
                }

                if (openInOffice is not null && openInOffice.Contains(path))
                {
                    // 아직 편집 중 → 대기열에 남겨 두고 닫힐 때까지 매 사이클 재확인.
                    MoaiLog.Debug($"FolderSync: defer {Tag(path)} (open in Office, sync on close)");
                    continue;
                }

                _pending.TryRemove(path, out var entry);
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

        // 이 파일의 직전 업로드 문서 id(있으면). 서버는 dedup 을 안 하므로, 재업로드 시 이 옛 문서를
        // 지워 문서함에 중복이 쌓이지 않게 한다(제자리 교체). 삭제는 새 업로드 성공 '후'에 해야
        // 업로드 실패 시 옛 문서가 그대로 남아 데이터가 사라지지 않는다.
        var oldDocId = _manifest.GetDocId(path);

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

            // 새 버전이 올라갔으니 옛 문서 제거(제자리 교체). best-effort — 실패해도 새 문서는 유효.
            if (!string.IsNullOrEmpty(oldDocId) && !string.Equals(oldDocId, docId, StringComparison.Ordinal))
            {
                await DeleteRemoteAsync(oldDocId, folder.OrgId).ConfigureAwait(false);
            }

            _manifest.MarkUploaded(path, docId);
            _manifest.Save();
            MoaiLog.Info($"FolderSync: upload ok {Tag(path)} docId={docId ?? "?"} replacedOld={(string.IsNullOrEmpty(oldDocId) ? "no" : "yes")}");
            Status?.Invoke($"문서함 반영됨: {name}");
        }
        else
        {
            MoaiLog.Warn($"FolderSync: upload failed {Tag(path)} reason={ClassifyFailure(last)}");
            Status?.Invoke($"실패: {name} — {last}");
        }
    }

    // 서버 문서 1건을 삭제(변경 재업로드 시 옛 버전 제거). OrgDocsDeleteTool 재사용, best-effort.
    private static async Task DeleteRemoteAsync(string docId, string? orgId)
    {
        try
        {
            var input = JsonSerializer.SerializeToElement(new { documentId = docId, orgId });
            var ctx = new ToolContext(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), PermissionMode.Auto);
            var ok = false;
            await foreach (var p in new OrgDocsDeleteTool().ExecuteAsync(input, ctx, CancellationToken.None).ConfigureAwait(false))
            {
                if (p is ToolOutput o)
                {
                    ok = !o.IsError;
                }
            }

            MoaiLog.Info($"FolderSync: prior-doc delete id={docId} ok={ok}");
        }
        catch (Exception ex)
        {
            MoaiLog.Warn($"FolderSync: prior-doc delete threw: {ex.GetType().Name}");
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
