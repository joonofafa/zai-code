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

    /// <summary>프로세스 종료를 기다리는 태스크(테스트/정리용).</summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    internal CancellationToken KillToken => _kill.Token;

    internal void Append(string line)
    {
        lock (_lock)
        {
            _buffer.Append(line).Append('\n');
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

    /// <summary>마지막 조회 이후 새로 쌓인 출력. truncated 는 그 사이 상한 초과로 일부가 버려졌음.</summary>
    public (string Text, bool Truncated) ReadNew()
    {
        lock (_lock)
        {
            var text = _buffer.ToString(_readPos, _buffer.Length - _readPos);
            _readPos = _buffer.Length;
            var truncated = _truncated;
            _truncated = false;
            return (text, truncated);
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
            if (Status != BackgroundShellStatus.Running)
            {
                return false;
            }

            Status = BackgroundShellStatus.Killed;
        }

        // CliWrap 은 토큰 취소 시 Process.Kill(entireProcessTree: true) 로 자식까지 정리한다.
        _kill.Cancel();
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

        var stdout = PipeTarget.ToDelegate(shell.Append, Encoding.UTF8);
        var run = Cli.Wrap(exe)
            .WithArguments(args)
            .WithWorkingDirectory(workingDirectory)
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(stdout)
            .WithStandardErrorPipe(stdout)   // 동기 Bash 와 같이 stdout/stderr 합산
            .ExecuteAsync(shell.KillToken)
            .Task;

        shell.Track(run);
        _shells[id] = shell;
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
