using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using MoaiCode.Localization;

namespace MoaiCode.Persistence;

public sealed record CheckpointInfo(string Id, string Subject, DateTimeOffset CreatedAt);

public sealed class CheckpointStore
{
    private static readonly string[] Excludes =
    {
        "/.git", "/.svn", "/.hg",
        "/bin", "/obj", "/node_modules", "/dist", "/build", "/target", "/vendor",
        "/.cache", "/.moai", "/.venv", "/venv", "/__pycache__", "/.next",
        "/.gradle", "/.nuget", "/.npm", "/.idea", "/.vs", "/.tox",
        "/.mypy_cache", "/.pytest_cache", "/.terraform",
        "*.exe", "*.dll", "*.so", "*.dylib", "*.pdb", "*.bin",
        "*.zip", "*.tar", "*.gz", "*.7z", "*.rar", "*.iso",
        "*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.ico",
        "*.mp4", "*.mov", "*.mkv", "*.webm", "*.wav", "*.mp3",
        "*.db", "*.sqlite", "*.sqlite3", "*.pack",
        "*.parquet", "*.onnx", "*.pt", "*.ckpt", "*.safetensors",
        ".env",
    };

    // OS별 경로 비교 (Windows 대소문자 무시).
    private static readonly StringComparison PathCmp =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// 체크포인트를 떠도 되는 워크스페이스인지. 홈 디렉토리·파일시스템 루트·홈의 상위 디렉토리는
    /// 거부한다 — 거기서 `git add -A` 하면 홈/디스크 전체를 스냅샷해 수십~수백 GB로 폭주한다(실측 117G 사고).
    /// </summary>
    public static bool IsCheckpointable(string workingDirectory)
    {
        string full;
        try
        {
            full = Path.GetFullPath(workingDirectory).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch
        {
            return false;
        }

        if (full.Length == 0)
        {
            return false;
        }

        // 파일시스템 루트(/, C:\) 거부.
        var root = (Path.GetPathRoot(full) ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar);
        if (full.Length <= root.Length)
        {
            return false;
        }

        var home = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
            .TrimEnd(Path.DirectorySeparatorChar);
        if (home.Length > 0)
        {
            // 홈 자체 거부.
            if (string.Equals(full, home, PathCmp))
            {
                return false;
            }

            // 홈의 상위(홈을 포함하는 조상) 거부 — 예: /home, /Users.
            if (home.StartsWith(full + Path.DirectorySeparatorChar, PathCmp))
            {
                return false;
            }
        }

        return true;
    }

    private static TimeSpan ResolveTimeout()
    {
        var v = Environment.GetEnvironmentVariable("MOAI_CHECKPOINT_TIMEOUT");
        return int.TryParse(v, out var s) && s > 0
            ? TimeSpan.FromSeconds(s)
            : TimeSpan.FromSeconds(20);
    }

    private readonly string _workingDirectory;
    private readonly string _gitDir;
    private readonly bool _enabled;
    // git 체크포인트 작업 상한. 거대한 워크스페이스에서 `git add -A`가 hang 되어 턴이 멈추는 것을 막는다.
    private readonly TimeSpan _gitTimeout;
    private readonly string _gitPath;
    private bool _timedOut; // 한 번 타임아웃되면 이후 체크포인트를 건너뛴다(매 툴마다 20초 대기 방지).

    // gitTimeout/gitPath 는 테스트 주입용(기본: env MOAI_CHECKPOINT_TIMEOUT 또는 20초, "git").
    public CheckpointStore(string workingDirectory, string? baseDir = null,
        TimeSpan? gitTimeout = null, string? gitPath = null)
    {
        _workingDirectory = Path.GetFullPath(workingDirectory);
        _enabled = IsCheckpointable(_workingDirectory);
        _gitTimeout = gitTimeout ?? ResolveTimeout();
        _gitPath = gitPath ?? "git";
        var root = baseDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".moai", "checkpoints");
        _gitDir = Path.Combine(root, Hash(_workingDirectory));
    }

    /// <summary>이 워크스페이스에서 체크포인트가 활성인지 (홈/루트 등은 비활성).</summary>
    public bool Enabled => _enabled;

    public async Task<string> CreateAsync(string subject, CancellationToken ct = default)
    {
        // 홈/루트 등 위험한 워크스페이스거나(디스크 폭주 방지), 이미 타임아웃돼 비활성이면 만들지 않는다.
        if (!_enabled || _timedOut)
        {
            return string.Empty;
        }

        try
        {
            await EnsureInitializedAsync(ct).ConfigureAwait(false);
            await GitAsync("add -A .", ct).ConfigureAwait(false);

            var status = await GitAsync("status --porcelain", ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(status.Output))
            {
                return await HeadAsync(ct).ConfigureAwait(false);
            }

            var msg = SanitizeSubject(subject);
            await RunGitAsync(new[] { "commit", "-m", msg }, ct).ConfigureAwait(false);
            return await HeadAsync(ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 워크스페이스가 너무 커서 git 이 상한을 넘겼다 → 이 세션에서 체크포인트를 끈다.
            _timedOut = true;
            Console.Error.WriteLine(
                L10n.Get("persistence.checkpointDisabledFmt", _gitTimeout.TotalSeconds));
            return string.Empty;
        }
    }

    public async Task<IReadOnlyList<CheckpointInfo>> ListAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_gitDir))
        {
            return Array.Empty<CheckpointInfo>();
        }

        var result = await GitAsync(
            "log --date=iso-strict --pretty=format:%h%x09%cd%x09%s -n 20",
            ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result.Output))
        {
            return Array.Empty<CheckpointInfo>();
        }

        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseLogLine)
            .Where(x => x is not null)
            .Select(x => x!)
            .ToList();
    }

    public async Task<string> DiffAsync(string? commit, CancellationToken ct = default)
    {
        if (!Directory.Exists(_gitDir))
        {
            return "(no checkpoints)";
        }

        var target = string.IsNullOrWhiteSpace(commit) ? "HEAD" : commit;
        var result = await GitAsync($"diff --stat {EscapeRev(target)} -- .", ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(result.Output) ? "(no differences)" : result.Output.TrimEnd();
    }

    public async Task RestoreAsync(string commit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(commit))
        {
            throw new ArgumentException("checkpoint id is required", nameof(commit));
        }

        if (!Directory.Exists(_gitDir))
        {
            throw new InvalidOperationException("no checkpoint store exists for this workspace");
        }

        // 완전 복원(rewind): 워킹트리를 해당 체크포인트와 정확히 일치시킨다.
        //  1) read-tree  : 인덱스를 그 시점 트리로 교체
        //  2) checkout-index -f -a : 인덱스 내용으로 워킹트리 파일을 덮어씀(누락 파일 복구)
        //  3) clean -fd  : 그 이후 생긴(인덱스에 없는) 파일을 제거 — 단순 checkout -- . 로는 안 지워짐
        // HEAD는 그대로 두므로 이후 체크포인트 이력은 보존된다. clean은 info/exclude(.git/bin/obj/.env 등)를 존중.
        var rev = EscapeRev(commit);
        await RunGitAsync(new[] { "read-tree", rev }, ct).ConfigureAwait(false);
        await RunGitAsync(new[] { "checkout-index", "-f", "-a" }, ct).ConfigureAwait(false);

        var cleanArgs = new List<string> { "clean", "-fd" };
        // 체크포인트 git 디렉토리가 워킹트리 내부에 있으면(테스트 등) clean이 그것까지 지우지 않도록 제외.
        var sep = _workingDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? _workingDirectory
            : _workingDirectory + Path.DirectorySeparatorChar;
        if (_gitDir.StartsWith(sep, StringComparison.Ordinal))
        {
            var rel = Path.GetRelativePath(_workingDirectory, _gitDir).Replace('\\', '/');
            cleanArgs.Add("-e");
            cleanArgs.Add("/" + rel);
        }

        await RunGitAsync(cleanArgs, ct).ConfigureAwait(false);
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (Directory.Exists(_gitDir))
        {
            return;
        }

        Directory.CreateDirectory(_gitDir);
        await GitAsync("init", ct).ConfigureAwait(false);
        await GitAsync("config user.email moai-code@local", ct).ConfigureAwait(false);
        await GitAsync("config user.name MoAI Code", ct).ConfigureAwait(false);

        var info = Path.Combine(_gitDir, "info");
        Directory.CreateDirectory(info);
        await File.WriteAllLinesAsync(Path.Combine(info, "exclude"), Excludes, ct).ConfigureAwait(false);
    }

    private async Task<string> HeadAsync(CancellationToken ct)
    {
        var result = await GitAsync("rev-parse --short HEAD", ct).ConfigureAwait(false);
        return result.Output.Trim();
    }

    // 공백/따옴표 없는 단순 명령용 편의 래퍼. 특수문자가 들어갈 수 있는 인자(커밋 메시지 등)는
    // RunGitAsync(argv)로 직접 넘겨 셸 문자열 재파싱을 피한다.
    private Task<(int ExitCode, string Output)> GitAsync(string args, CancellationToken ct)
        => RunGitAsync(SplitArgs(args).ToList(), ct);

    private async Task<(int ExitCode, string Output)> RunGitAsync(IReadOnlyList<string> argv, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_gitPath)
        {
            WorkingDirectory = _workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add($"--git-dir={_gitDir}");
        psi.ArgumentList.Add($"--work-tree={_workingDirectory}");
        foreach (var part in argv)
        {
            psi.ArgumentList.Add(part);
        }

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("failed to start git");

        // 상한을 건다: 거대한 워크스페이스에서 git 이 hang 되어 턴이 무한 정지하는 것을 막는다.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_gitTimeout);

        // stdout/stderr를 동시에 읽어 파이프 버퍼가 차서 생기는 교착을 막는다. 읽기는 취소 토큰에
        // 묶지 않는다 — 프로세스를 죽이면 스트림이 닫혀 자연히 완료된다(버려진 읽기가 faults 되지 않도록).
        var stdoutTask = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = p.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await p.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            TryKill(p);
            throw new TimeoutException(
                L10n.Get("persistence.gitTimeoutFmt", _gitTimeout.TotalSeconds, string.Join(' ', argv)));
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        var output = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(output.Trim());
        }

        return (p.ExitCode, output);
    }

    private static void TryKill(Process p)
    {
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best-effort — 이미 종료됐거나 권한 문제. 방치해도 상한 후 OS가 정리.
        }
    }

    private static CheckpointInfo? ParseLogLine(string line)
    {
        var parts = line.Split('\t', 3);
        if (parts.Length != 3 || !DateTimeOffset.TryParse(parts[1], out var created))
        {
            return null;
        }

        return new CheckpointInfo(parts[0], parts[2], created);
    }

    private static string SanitizeSubject(string subject)
        => string.IsNullOrWhiteSpace(subject)
            ? "checkpoint"
            : subject.Replace('\n', ' ').Replace('\r', ' ').Trim();

    private static string EscapeRev(string value)
    {
        if (value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '/'))
        {
            return value;
        }

        throw new ArgumentException("invalid checkpoint id");
    }

    private static IEnumerable<string> SplitArgs(string args)
    {
        var current = new StringBuilder();
        var inQuote = false;
        for (var i = 0; i < args.Length; i++)
        {
            var c = args[i];
            if (c == '"')
            {
                inQuote = !inQuote;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuote)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                continue;
            }

            if (c == '\\' && i + 1 < args.Length)
            {
                i++;
                current.Append(args[i]);
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
