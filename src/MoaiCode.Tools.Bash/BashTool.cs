using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CliWrap;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;

namespace MoaiCode.Tools.Bash;

#if !WINDOWS
// 유닉스 시그널 P/Invoke — kill(-PGID, SIGKILL) 프로세스 그룹 타격용.
internal static partial class UnixSignal
{
    public const int SIGKILL = 9;

    [DllImport("libc", SetLastError = true)]
    internal static extern int kill(int pid, int signal);
}
#endif

/// <summary>
/// 크로스플랫폼 셸 실행 툴. CliWrap 기반. 보안은 BashSecurity 단순화 정책.
/// 설계 근거: ../../CSHARP_PORT_PLAN.md 4.5.
/// </summary>
public sealed class BashTool : ITool
{
    private const int DefaultTimeoutMs = 120_000;
    private const int MaxTimeoutMs = 600_000;   // 상한 10분 — 모델이 무한대 타임아웃을 넣어 턴이 멈추는 것 방지.
    private const int MaxOutputChars = 30_000;
    private const int MaxBufferChars = 200_000; // 수집 버퍼 상한 — 모델 노출(30k)보다 여유. 파이프 홀더 생존 시 무한 증가 방지.
    private const string CwdMarker = "__MOAI_CWD__:";

    // 세션 동안 작업 디렉터리를 유지한다 (description의 "persists between commands" 보장).
    // 직전 명령이 cd로 옮긴 위치를 다음 명령에서도 이어 쓴다. 엔진이 툴을 순차 실행하므로 경합 없음.
    private string? _currentDir;
    private readonly BackgroundShellRegistry _background;

    public BashTool(BackgroundShellRegistry? background = null)
    {
        _background = background ?? BackgroundShellRegistry.Shared;
    }

    public string Name => "Bash";

    public string Description => """
        Executes a given bash command and returns its combined stdout/stderr.

        The working directory persists between commands, but shell state (env vars, functions) does not.

        IMPORTANT: Avoid using this tool to run find, grep, cat, head, tail, sed, awk, or echo commands, unless explicitly instructed or after you have verified that a dedicated tool cannot accomplish your task. Instead, use the appropriate dedicated tool:
        - File search: use Glob (NOT find or ls)
        - Content search: use Grep (NOT grep or rg)
        - Read files: use Read (NOT cat/head/tail)
        - Edit files: use Edit (NOT sed/awk)
        - Write files: use Write (NOT echo >/cat <<EOF)
        - Communication: output text directly (NOT echo/printf)

        Reserve Bash for system commands and terminal operations that require shell execution. This runs ANY shell command available on this machine, including network and remote tools — ssh, scp, rsync, curl, git, and database clients (psql, mysql, redis-cli, etc.). If the user asks for something the shell can do (e.g. "ssh into host X and run a query"), DO IT with Bash; do NOT claim you lack the capability. The shell is your capability.

        Very high-risk commands (rm -rf on root/home, dd to block devices, mkfs, fork bombs, remote-script piping) are blocked.

        Long-running commands (servers, watchers, `tail -f`, log monitors, long builds): set `run_in_background: true`. The tool returns a shell id immediately instead of blocking; read new output later with BashOutput and stop it with KillShell. Background shells do not carry `cd` over to the next command and are terminated when the session exits.
        """;

    public bool IsReadOnly => false;
    public bool IsConcurrencySafe => false;

    public JsonElement InputSchema { get; } = Parse(
        """
        {
          "type": "object",
          "properties": {
            "command": { "type": "string", "description": "Shell command to execute" },
            "timeout_ms": { "type": "integer", "description": "Timeout in milliseconds (default 120000). Ignored when run_in_background is true" },
            "run_in_background": { "type": "boolean", "description": "Start the command detached and return a shell id immediately; poll with BashOutput, stop with KillShell" }
          },
          "required": ["command"]
        }
        """);

    private sealed record Input(
        [property: JsonPropertyName("command")] string? Command,
        [property: JsonPropertyName("timeout_ms")] int? TimeoutMs,
        [property: JsonPropertyName("run_in_background")] bool? RunInBackground);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        var inp = input.Deserialize<Input>();
        if (inp is null || string.IsNullOrWhiteSpace(inp.Command))
        {
            yield return new ToolOutput("Bash: 'command' is required", IsError: true);
            yield break;
        }

        var command = inp.Command;

        // 1) 고위험 패턴은 권한 모드와 무관하게 차단
        var verdict = BashSecurity.Check(command);
        if (!verdict.Allowed)
        {
            yield return new ToolOutput(L10n.Get("tools.bash.denied", verdict.Reason), IsError: true);
            yield break;
        }

        // 2) 권한 모드 연동 (스켈레톤: deny면 차단, 그 외 허용)
        //    Phase 5에서 ask 모드의 대화형 승인 UI 연결.
        if (context.Permission == PermissionMode.Deny && !BashSecurity.IsReadOnlyCommand(command))
        {
            yield return new ToolOutput(L10n.Get("tools.bash.deniedDenyMode"), IsError: true);
            yield break;
        }

        // 유지된 cwd를 우선 사용하되, 그 폴더가 사라졌으면(삭제/이동) ENOENT 방지를 위해 폴백.
        _currentDir ??= context.WorkingDirectory;
        var workDir = ResolveWorkDir(_currentDir);

        // 3) 백그라운드: 보안·권한 게이트는 위에서 동일하게 통과한 뒤, 프로세스를 띄우고 즉시 id 만 돌려준다.
        if (inp.RunInBackground == true)
        {
            var bg = _background.Start(command, workDir);
            yield return new ToolOutput(L10n.Get("tools.bash.startedBackground", bg.Id));
            yield break;
        }

        var (shell, args) = ResolveShell(command);
        // 모델이 제어하는 값이라 상한을 건다(무한/과대 타임아웃으로 턴이 멈추는 것 방지).
        var timeout = Math.Clamp(inp.TimeoutMs ?? DefaultTimeoutMs, 1_000, MaxTimeoutMs);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        // 파이프로 직접 출력을 수집한다 — 타임아웃/취소 시에도 그 시점까지 받은 출력을
        // 버리지 않고 부분 결과로 돌려줄 수 있다(ExecuteBufferedAsync 는 예외 시 출력을 잃음).
        // 버퍼는 파이프 delegate(다른 스레드)와 경합하므로 락으로 보호하고, 살아남은 데몬이
        // 파이프를 계속 채우는 경우를 대비해 용량 상한을 둔다(무한 증가 방지).
        var stdoutBuf = new StringBuilder();
        var stderrBuf = new StringBuilder();
        var bufLock = new object();
        var bufCapped = false;
        void Append(StringBuilder buf, char[] chunk, int count)
        {
            lock (bufLock)
            {
                var room = MaxBufferChars - buf.Length;
                if (count > room)
                {
                    bufCapped = true;   // 이후 출력은 버린다 — 부분 결과는 이미 충분히 커다란 의미.
                }

                if (room > 0)
                {
                    buf.Append(chunk, 0, Math.Min(count, room));
                }
            }
        }

        // 줄 단위(ToDelegate)가 아니라 조각 단위로 읽는다 — 줄바꿈 없는 거대한 출력(압축된 대형 파일·바이너리)은
        // 한 줄이 끝날 때까지 통째로 메모리에 쌓였다(상한 검사는 줄 사이에서만). 상한을 넘어도 끝까지 읽어 버려
        // 자식이 가득 찬 파이프에 막히지 않게 한다.
        PipeTarget Capture(StringBuilder buf) => PipeTarget.Create(async (stream, token) =>
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
            var chunk = new char[8192];
            int n;
            while ((n = await reader.ReadAsync(chunk.AsMemory(), token).ConfigureAwait(false)) > 0)
            {
                Append(buf, chunk, n);
            }
        });

        string Snapshot()
        {
            lock (bufLock)
            {
                var combined = Combine(stdoutBuf, stderrBuf);
                if (bufCapped)
                {
                    combined += "\n… (output capped)";
                }

                return combined;
            }
        }
        var exitCode = 0;
        var timedOut = false;
        try
        {
            var (exe, exeArgs) = InNewProcessGroup(shell, args);
            var cmd = Cli.Wrap(exe)
                .WithArguments(exeArgs)
                .WithWorkingDirectory(workDir)
                .WithValidation(CommandResultValidation.None)
                // 자식 출력은 UTF-8로 디코딩(Windows는 위에서 chcp 65001로 UTF-8 정규화, Unix는 기본 UTF-8).
                .WithStandardOutputPipe(Capture(stdoutBuf))
                .WithStandardErrorPipe(Capture(stderrBuf));
            // 주의: 명령이 띄운 백그라운드 프로세스가 파이프 쓰기 끝을 물고 있으면 메인 프로세스가
            // 끝난 뒤에도 EOF 가 오지 않아 ExecuteAsync 대기가 풀리지 않는다(무한 대기 버그, 2026-09-25).
            // CancelAfter 토큰만으로는 이 대기를 깨울 수 없으므로 타임아웃은 독립 레이스로 판정하고,
            // 지면 트리를 kill 한 뒤 commandTask 를 기다리지 않고 부분 출력을 즉시 반환한다.
            var execution = cmd.ExecuteAsync(timeoutCts.Token);
            var winner = await Task.WhenAny(execution.Task, Task.Delay(timeout, CancellationToken.None)).ConfigureAwait(false);
            if (winner != execution.Task)
            {
                timedOut = true;
                KillProcessTree(execution.ProcessId, timeoutCts);
                // 트리 종료로 파이프가 닫히면 마지막 출력이 흘러들어온다 — 수집 여유만 짧게 준다.
                await Task.WhenAny(execution.Task, Task.Delay(500, CancellationToken.None)).ConfigureAwait(false);
                // 이후 수집은 Append 의 용량 상한(MaxBufferChars)이 이미 담당한다 —
                // 살아남은 파이프 홀더가 계속 써도 버퍼는 유한하게 멈춘다.
                // 기다리지 않고 반환하므로 나중에 완료될 태스크의 예외를 미리 관찰해둔다.
                _ = execution.Task.ContinueWith(
                    t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            }
            else
            {
                var result = await execution.Task.ConfigureAwait(false);
                exitCode = result.ExitCode;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
        }

        if (timedOut)
        {
            // 타임아웃이어도 지금까지 쌓인 출력은 모델에게 보여준다(진단/재시도 근거).
            var partial = Truncate(Snapshot());
            var msg = L10n.Get("tools.bash.timeout", timeout);
            if (partial.Length > 0)
            {
                msg += "\n" + partial;
            }

            yield return new ToolOutput(msg, IsError: true);
            yield break;
        }

        // 명령 실행 후의 cwd를 마커에서 추출해 유지한다 (출력에서는 마커 제거).
        // 정상 경로는 프로세스가 끝났어도 파이프 drain 이 완료된 뒤다(delegate 경합 없음).
        var (_, newCwd) = ExtractCwd(stdoutBuf.ToString());
        if (newCwd is not null && Directory.Exists(newCwd))
        {
            _currentDir = newCwd;
        }

        var text = Truncate(Combine(stdoutBuf, stderrBuf));
        if (text.Length == 0)
        {
            text = $"(no output, exit code {exitCode})";
        }

        yield return new ToolOutput(text, IsError: exitCode != 0);
    }

    // stdout(stderr 포함) 조합 — stdout 에서는 cwd 마커를 제거한 뒤 합친다.
    private static string Combine(StringBuilder stdoutBuf, StringBuilder stderrBuf)
    {
        var stdout = ExtractCwd(stdoutBuf.ToString()).Output.TrimEnd('\n', '\r');
        var combined = new StringBuilder();
        if (!string.IsNullOrEmpty(stdout))
        {
            combined.Append(stdout);
        }

        var stderr = stderrBuf.ToString();
        if (!string.IsNullOrEmpty(stderr))
        {
            if (combined.Length > 0)
            {
                combined.AppendLine();
            }

            combined.Append(stderr);
        }

        return combined.ToString();
    }

    // Unix: setsid 로 새 세션·프로세스 그룹에서 띄운다. 그룹 리더 PID == PGID 이므로 종료 시
    // kill(-PGID) 로 데몬(재부모된 자식 포함)까지 확정 타격할 수 있다(KillProcessTree).
    // setsid 바이너리가 없는 플랫폼(macOS 등)·Windows 는 셸을 직접 띄운다(트리 kill 폴백이 담당).
    // 백그라운드 셸(BackgroundShellRegistry)도 같은 방식으로 띄운다.
    internal static (string Exe, string[] Args) InNewProcessGroup(string shell, string[] args)
    {
        var setsid = File.Exists("/usr/bin/setsid") ? "/usr/bin/setsid" : null;
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || setsid is null
            ? (shell, args)
            : (setsid, new[] { shell }.Concat(args).ToArray());
    }

    // 타임아웃 판정 시 프로세스와 자손 전체를 종료한다. 셸을 setsid(새 세션·프로세스 그룹)로
    // 띄웠으므로 그룹 리더 PID == PGID 이고, kill(-PGID) 로 그룹 전체를 한 번에 친다 —
    // 명령이 띄운 데몬이 init 에 재부모되어도 PGID 는 유지되므로 파이프 홀더까지 확정 타격된다.
    // 그룹 킬에 실패했을 때의 폴백으로 /proc children 순회 킬을 유지한다.
    internal static void KillProcessTree(int processId, CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();   // CliWrap 이 메인 프로세스를 kill 하게 한다.
        }
        catch
        {
            // best effort
        }

        // PID 를 모르면(0) 여기서 멈춘다 — kill(-0) 은 '호출자 자신의 프로세스 그룹' 이라 zaiCode 까지 죽인다.
        if (processId <= 0)
        {
            return;
        }

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var _ = Process.Start(new ProcessStartInfo("taskkill", $"/PID {processId} /T /F")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
                return;
            }

            // 새 세션 리더이므로 PID == PGID. 부정적 kill(-PGID, 0) 로 그룹 존재 확인 후 SIGKILL.
            if (UnixSignal.kill(-processId, 0) == 0)
            {
                UnixSignal.kill(-processId, UnixSignal.SIGKILL);
                return;
            }
        }
        catch
        {
            // best effort — 이 실패가 도구 반환을 막아서는 안 된다.
        }

        try
        {
            KillSelfAndDescendants(processId);
        }
        catch
        {
            // best effort
        }
    }

    // leaf(가장 깊은 자손)부터 SIGKILL — /proc/<pid>/task/*/children 로 자식을 찾는다.
    private static void KillSelfAndDescendants(int pid)
    {
        List<int> children;
        try
        {
            children = new List<int>();
            foreach (var taskDir in Directory.GetDirectories($"/proc/{pid}/task"))
            {
                var childrenFile = Path.Combine(taskDir, "children");
                if (!File.Exists(childrenFile))
                {
                    continue;
                }

                foreach (var token in File.ReadAllText(childrenFile).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (int.TryParse(token, out var child))
                    {
                        children.Add(child);
                    }
                }
            }
        }
        catch
        {
            return; // 이미 종료된 프로세스
        }

        foreach (var child in children)
        {
            KillSelfAndDescendants(child);
        }

        try
        {
            Process.GetProcessById(pid).Kill();
        }
        catch
        {
            // already gone / not ours
        }
    }

    // cwd가 유효하지 않으면(삭제/이동) 프로세스 cwd → 홈 순으로 폴백.
    private static string ResolveWorkDir(string dir)
    {
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            return dir;
        }

        try
        {
            var cwd = Directory.GetCurrentDirectory();
            if (Directory.Exists(cwd))
            {
                return cwd;
            }
        }
        catch
        {
            // ignore
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static (string Shell, string[] Args) ResolveShell(string command)
    {
        // 명령 뒤에 현재 디렉터리를 마커로 출력해, 명령 내부의 cd 효과를 다음 호출까지 유지한다.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // 한국어 Windows에서 cmd 내장 메시지('x'은(는) ... 아닙니다 등)는 OEM 코드페이지(CP949)로
            // 나가서, UTF-8로 디코딩하면 깨진다. `chcp 65001`로 출력을 UTF-8로 정규화한 뒤 실행한다
            // (>nul 로 "Active code page" 배너 숨김, &로 chcp 실패해도 명령은 진행). 마커의 %cd%(한글 경로 포함)도 UTF-8로 정상화.
            return ("cmd.exe", new[] { "/c", "chcp 65001>nul & " + command + " & echo " + CwdMarker + "%cd%" });
        }

        var bash = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
        return (bash, new[] { "-c", command + "\nprintf '" + CwdMarker + "%s\\n' \"$(pwd)\"" });
    }

    // stdout 끝의 cwd 마커를 떼어내 (정리된 출력, 새 cwd)로 반환.
    private static (string Output, string? Cwd) ExtractCwd(string stdout)
    {
        var idx = stdout.LastIndexOf(CwdMarker, StringComparison.Ordinal);
        if (idx < 0)
        {
            return (stdout, null);
        }

        var after = stdout[(idx + CwdMarker.Length)..];
        var nl = after.IndexOfAny(new[] { '\n', '\r' });
        var path = (nl >= 0 ? after[..nl] : after).Trim();
        var before = stdout[..idx].TrimEnd('\n', '\r');
        return (before, string.IsNullOrEmpty(path) ? null : path);
    }

    private static string Truncate(string s)
        => s.Length <= MaxOutputChars
            ? s
            : s[..MaxOutputChars] + $"\n… (truncated, {s.Length - MaxOutputChars} more chars)";

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
