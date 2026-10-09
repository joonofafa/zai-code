using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using CliWrap;

namespace MoaiCode.Tools.Bash;

public enum BackgroundShellStatus
{
    Running,
    Completed,
    Killed,
}

/// <summary>백그라운드 셸 완료 통보 — 대기 복귀(자동 재개 턴)의 트리거 데이터.</summary>
public sealed record BackgroundShellFinished(string Id, string Command, BackgroundShellStatus Status, int? ExitCode);

/// <summary>
/// 백그라운드 셸 1개: 프로세스 핸들 + 스트리밍 출력 버퍼 + 읽기 커서.
/// 출력은 자식이 쓰는 즉시 버퍼에 쌓이고, <see cref="ReadNew"/> 가 마지막 조회 이후 새 부분만 돌려준다.
/// </summary>
public sealed class BackgroundShell
{
    private readonly StringBuilder _buffer = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _kill = new();
    private readonly int _maxChars;
    private int _readPos;
    private bool _truncated;

    internal BackgroundShell(string id, string command, int maxChars)
    {
        Id = id;
        Command = command;
        _maxChars = maxChars;
    }

    public string Id { get; }
    public string Command { get; }
    public BackgroundShellStatus Status { get; private set; } = BackgroundShellStatus.Running;
    public int? ExitCode { get; private set; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// 모델이 이 셸의 최종 상태를 이미 안다 — KillShell 로 직접 죽였거나, 종료 후 BashOutput 으로 끝까지 읽었다.
    /// 참이면 완료 통보(자동 재개 턴)가 필요 없다.
    /// </summary>
    public bool Acknowledged { get; private set; }

    /// <summary>프로세스 종료를 기다리는 태스크(테스트/정리용).</summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    internal CancellationToken KillToken => _kill.Token;

    /// <summary>셸 프로세스 PID(setsid 로 띄웠으면 PGID 와 같다) — 그룹 종료용.</summary>
    internal int ProcessId { get; set; }

    // 출력 조각을 그대로 붙인다(줄 단위로 모으지 않는다 — 줄바꿈 없는 거대한 출력이 한 줄 통째로 메모리에 쌓이지 않게).
    internal void AppendText(ReadOnlySpan<char> text)
    {
        lock (_lock)
        {
            _buffer.Append(text);
            if (_buffer.Length > _maxChars)
            {
                // 상한 초과 시 앞부분(오래된 출력)을 버리고 꼬리를 남긴다. 아직 안 읽은 구간이 잘렸으면 표시.
                var drop = _buffer.Length - _maxChars;
                _buffer.Remove(0, drop);
                if (_readPos < drop)
                {
                    _truncated = true;
                }

                _readPos = Math.Max(0, _readPos - drop);
            }
        }
    }

    /// <summary>아직 읽지 않은 출력 길이(소비하지 않음).</summary>
    public int UnreadLength
    {
        get { lock (_lock) { return _buffer.Length - _readPos; } }
    }

    /// <summary>
    /// 마지막 조회 이후 새로 쌓인 출력. truncated 는 그 사이 상한 초과로 일부가 버려졌음.
    /// Status/ExitCode 는 읽은 시점의 상태 — 읽은 뒤 따로 조회하면 그 사이 종료돼 "완료"인데 끝 출력은
    /// 못 받은 어긋난 보고가 된다.
    /// </summary>
    public (string Text, bool Truncated, BackgroundShellStatus Status, int? ExitCode) ReadNew()
    {
        lock (_lock)
        {
            var text = _buffer.ToString(_readPos, _buffer.Length - _readPos);
            _readPos = _buffer.Length;
            // 종료 후 읽기 = 남은 출력과 최종 상태를 모델이 다 봤다(Status 는 같은 락 안에서 확정된다).
            if (Status != BackgroundShellStatus.Running)
            {
                Acknowledged = true;
            }

            var truncated = _truncated;
            _truncated = false;
            return (text, truncated, Status, ExitCode);
        }
    }

    internal void Track(Task<CommandResult> run)
    {
        Completion = run.ContinueWith(t =>
        {
            lock (_lock)
            {
                if (Status == BackgroundShellStatus.Running)
                {
                    if (t.IsCompletedSuccessfully)
                    {
                        Status = BackgroundShellStatus.Completed;
                        ExitCode = t.Result.ExitCode;
                    }
                    else
                    {
                        // CliWrap 은 취소 시 OperationCanceledException — Kill() 이 이미 상태를 세팅한다.
                        // 그 외(시작 실패 등)는 completed + exit code 없음으로 마감.
                        Status = _kill.IsCancellationRequested ? BackgroundShellStatus.Killed : BackgroundShellStatus.Completed;
                    }
                }
            }
        }, TaskScheduler.Default);
    }

    /// <summary>프로세스 트리를 강제 종료. 이미 끝났으면 false.</summary>
    public bool Kill()
    {
        lock (_lock)
        {
            Acknowledged = true;   // 직접 kill(이미 끝났으면 KillShell 이 최종 상태를 알려준다) — 결과를 이미 안다
            if (Status != BackgroundShellStatus.Running)
            {
                return false;
            }

            Status = BackgroundShellStatus.Killed;
        }

        // 동기 Bash 와 같은 종료: CliWrap 취소 + 프로세스 그룹(setsid) 통째 SIGKILL. 트리 kill 만으로는
        // `서버 &`·nohup 처럼 init 에 재부모된 자식이 살아남아 포트·파이프를 계속 쥐었다.
        BashTool.KillProcessTree(ProcessId, _kill);
        return true;
    }
}

/// <summary>
/// 백그라운드 셸 원장. Bash(run_in_background) 가 등록하고 BashOutput/KillShell 이 조회·종료한다.
/// 프로세스는 moai 수명에 종속 — 프로세스 종료 시 남은 셸을 모두 kill 한다(고아 방지).
/// </summary>
public sealed class BackgroundShellRegistry
{
    public const int DefaultMaxChars = 200_000;

    public static BackgroundShellRegistry Shared { get; } = new();

    private const int MaxTrackedShells = 50;   // 완료 셸 무한 누적 방지 상한

    private readonly ConcurrentDictionary<string, BackgroundShell> _shells = new();
    private readonly int _maxChars;
    private int _seq;

    /// <summary>
    /// 셸 완료(정상/킬 포함) 시 UI 스레드에 전달되는 콜백. null 이면 통보 없음.
    /// ReplApp 이 구독해 "백그라운드 완료 → 자동 재개 터"을 구동한다.
    /// </summary>
    public Action<BackgroundShellFinished>? OnShellFinished { get; set; }

    public BackgroundShellRegistry(int maxChars = DefaultMaxChars)
    {
        _maxChars = maxChars;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => KillAll();
    }

    public BackgroundShell Start(string command, string workingDirectory)
    {
        var id = $"bash_{Interlocked.Increment(ref _seq)}";
        var shell = new BackgroundShell(id, command, _maxChars);
        var (exe, args) = ResolveShell(command);

        var stdout = PipeTarget.Create(async (stream, token) =>
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
            var chunk = new char[8192];
            int n;
            while ((n = await reader.ReadAsync(chunk.AsMemory(), token).ConfigureAwait(false)) > 0)
            {
                shell.AppendText(chunk.AsSpan(0, n));
            }
        });
        (exe, args) = BashTool.InNewProcessGroup(exe, args);   // 그룹 종료(Kill)가 떨어져 나간 자식까지 닿게
        var execution = Cli.Wrap(exe)
            .WithArguments(args)
            .WithWorkingDirectory(workingDirectory)
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(stdout)
            .WithStandardErrorPipe(stdout)   // 동기 Bash 와 같이 stdout/stderr 합산
            .ExecuteAsync(shell.KillToken);

        shell.ProcessId = execution.ProcessId;
        shell.Track(execution.Task);
        _shells[id] = shell;

        // 완료 통보: Track 이 Status/ExitCode 를 확정한 뒤 같은 continuation 체인에서 발화.
        // 스레드풀 스레드에서 호출되므로 구독자(ReplApp)는 자체 마샬링 필요.
        if (OnShellFinished is not null)
        {
            _ = shell.Completion.ContinueWith(
                _ => OnShellFinished(new BackgroundShellFinished(
                    shell.Id, shell.Command, shell.Status, shell.ExitCode)),
                TaskScheduler.Default);
        }

        Prune();
        return shell;
    }

    // 상한 초과 시 오래된 종료(completed/killed) 셸부터 제거. 실행 중 셸은 절대 건드리지 않고,
    // 최근 종료 셸은 모델이 BashOutput 으로 늦게 읽을 수 있으므로 즉시 지우지 않고 상한으로 관리.
    private void Prune()
    {
        if (_shells.Count <= MaxTrackedShells)
        {
            return;
        }

        var done = _shells.Values
            .Where(s => s.Status != BackgroundShellStatus.Running)
            .OrderBy(s => s.StartedAt)
            .ToList();
        foreach (var s in done)
        {
            if (_shells.Count <= MaxTrackedShells)
            {
                break;
            }

            _shells.TryRemove(s.Id, out _);
        }
    }

    public BackgroundShell? Get(string id) => _shells.TryGetValue(id, out var s) ? s : null;

    public IReadOnlyCollection<BackgroundShell> All => _shells.Values.ToArray();

    public void KillAll()
    {
        foreach (var s in _shells.Values)
        {
            s.Kill();
        }
    }

    private static (string Shell, string[] Args) ResolveShell(string command)
    {
        // 동기 BashTool 과 동일한 셸·UTF-8 정규화. cwd 마커는 백그라운드엔 불필요(다음 명령으로 cd 를 이어가지 않음).
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return ("cmd.exe", new[] { "/c", "chcp 65001>nul & " + command });
        }

        var bash = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
        return (bash, new[] { "-c", command });
    }
}
