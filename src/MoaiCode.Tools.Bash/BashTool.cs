using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CliWrap;
using CliWrap.Buffered;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;

namespace MoaiCode.Tools.Bash;

/// <summary>
/// 크로스플랫폼 셸 실행 툴. CliWrap 기반. 보안은 BashSecurity 단순화 정책.
/// 설계 근거: ../../CSHARP_PORT_PLAN.md 4.5.
/// </summary>
public sealed class BashTool : ITool
{
    private const int DefaultTimeoutMs = 120_000;
    private const int MaxTimeoutMs = 600_000;   // 상한 10분 — 모델이 무한대 타임아웃을 넣어 턴이 멈추는 것 방지.
    private const int MaxOutputChars = 30_000;
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

        BufferedCommandResult result;
        var timedOut = false;
        try
        {
            result = await Cli.Wrap(shell)
                .WithArguments(args)
                .WithWorkingDirectory(workDir)
                .WithValidation(CommandResultValidation.None)
                // 자식 출력은 UTF-8로 디코딩(Windows는 위에서 chcp 65001로 UTF-8 정규화, Unix는 기본 UTF-8).
                // 앰비언트 Console.OutputEncoding 에 의존하지 않도록 명시.
                .ExecuteBufferedAsync(Encoding.UTF8, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            result = null!;
        }

        if (timedOut)
        {
            yield return new ToolOutput(L10n.Get("tools.bash.timeout", timeout), IsError: true);
            yield break;
        }

        // 명령 실행 후의 cwd를 마커에서 추출해 유지한다 (출력에서는 마커 제거).
        var (stdout, newCwd) = ExtractCwd(result.StandardOutput ?? "");
        if (newCwd is not null && Directory.Exists(newCwd))
        {
            _currentDir = newCwd;
        }

        var combined = new StringBuilder();
        if (!string.IsNullOrEmpty(stdout))
        {
            combined.Append(stdout);
        }

        if (!string.IsNullOrEmpty(result.StandardError))
        {
            if (combined.Length > 0)
            {
                combined.AppendLine();
            }

            combined.Append(result.StandardError);
        }

        var text = Truncate(combined.ToString());
        if (text.Length == 0)
        {
            text = $"(no output, exit code {result.ExitCode})";
        }

        yield return new ToolOutput(text, IsError: result.ExitCode != 0);
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
