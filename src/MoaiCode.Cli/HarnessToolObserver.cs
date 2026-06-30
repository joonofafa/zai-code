using System.Diagnostics;
using System.Text;
using MoaiCode.Config;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;
using MoaiCode.Persistence;

namespace MoaiCode.Cli;

public sealed class HarnessToolObserver : IToolObserver
{
    private const int MaxOutput = 12000;
    private readonly Settings _settings;
    private readonly CheckpointStore _checkpoints;

    // 이번 턴에 파일 수정(Edit/Write)이 있었는지. 검증을 매 편집마다가 아니라 턴당 1회만 돌리기 위한 플래그.
    private bool _dirty;

    public HarnessToolObserver(Settings settings, CheckpointStore checkpoints)
    {
        _settings = settings;
        _checkpoints = checkpoints;
    }

    public async ValueTask BeforeToolAsync(
        ITool tool,
        ToolUseBlock call,
        ToolContext context,
        CancellationToken ct)
    {
        if (tool.IsReadOnly || !_settings.Checkpoints)
        {
            return;
        }

        try
        {
            await _checkpoints.CreateAsync($"before {tool.Name}", ct).ConfigureAwait(false);
        }
        catch
        {
            // Checkpoints are a safety net. Tool execution should not fail just because
            // git is unavailable or checkpoint initialization times out.
        }
    }

    public ValueTask<string?> AfterToolAsync(
        ITool tool,
        ToolUseBlock call,
        ToolContext context,
        string output,
        bool isError,
        CancellationToken ct)
    {
        // 검증은 AfterTurnAsync에서 턴당 1회 수행한다. 여기서는 수정 발생 여부만 기록.
        if (!isError && tool.Name is "Edit" or "Write")
        {
            _dirty = true;
        }

        return ValueTask.FromResult<string?>(null);
    }

    public async ValueTask<string?> AfterTurnAsync(ToolContext context, CancellationToken ct)
    {
        if (!_dirty)
        {
            return null;
        }

        _dirty = false;

        var observations = new List<string>();
        if (_settings.AutoLint && !string.IsNullOrWhiteSpace(_settings.LintCommand))
        {
            observations.Add(await RunVerificationAsync("lint", _settings.LintCommand!, context.WorkingDirectory, ct)
                .ConfigureAwait(false));
        }

        if (_settings.AutoTest && !string.IsNullOrWhiteSpace(_settings.TestCommand))
        {
            observations.Add(await RunVerificationAsync("test", _settings.TestCommand!, context.WorkingDirectory, ct)
                .ConfigureAwait(false));
        }

        observations = observations.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (observations.Count == 0)
        {
            return null;
        }

        return "<system-reminder>\n" +
               "Automatic verification ran after the file edits in this turn. Use these results before deciding " +
               "whether the task is complete. If verification failed, fix the failure or report it clearly.\n\n" +
               string.Join("\n\n", observations) +
               "\n</system-reminder>";
    }

    private static async Task<string> RunVerificationAsync(
        string label,
        string command,
        string workingDirectory,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ResolveShell(), ResolveArgs(command))
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var p = Process.Start(psi);
        if (p is null)
        {
            return $"{label}: failed to start `{command}`";
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));

        try
        {
            var stdoutTask = p.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = p.StandardError.ReadToEndAsync(timeout.Token);
            await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var text = (await stdoutTask.ConfigureAwait(false)) +
                       (await stderrTask.ConfigureAwait(false));
            text = Truncate(text.Trim());
            return $"{label}: `{command}` exited {p.ExitCode}\n{text}";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch
            {
                // best-effort
            }

            return $"{label}: `{command}` timed out after 300000ms";
        }
    }

    private static string ResolveShell()
        => OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash";

    private static string ResolveArgs(string command)
        => OperatingSystem.IsWindows() ? "/c " + command : "-c " + Quote(command);

    private static string Quote(string command)
        => "\"" + command.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string Truncate(string s)
        => s.Length <= MaxOutput ? s : s[..MaxOutput] + $"\n... (truncated, {s.Length - MaxOutput} more chars)";
}
