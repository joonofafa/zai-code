using System.Diagnostics;
using System.Threading.Channels;

namespace MoaiCode.Mcp;

/// <summary>MCP stdio 전송: 서버 프로세스를 spawn하고 stdin/stdout으로 줄 단위 JSON 교환.</summary>
public sealed class StdioTransport : IMcpTransport
{
    private readonly Process _proc;
    private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private StdioTransport(Process proc)
    {
        _proc = proc;
        _ = ReadLoopAsync();
        _ = DrainStdErrAsync();
    }

    public ChannelReader<string> Incoming => _incoming.Reader;

    public static StdioTransport Start(McpServerConfig config)
    {
        var psi = new ProcessStartInfo
        {
            FileName = config.Command,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var arg in config.Args)
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (var (k, v) in config.Env)
        {
            psi.Environment[k] = v;
        }

        var proc = Process.Start(psi)
                   ?? throw new McpException($"Failed to start MCP server: {config.Command}");
        return new StdioTransport(proc);
    }

    public async Task SendAsync(string message, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _proc.StandardInput.WriteLineAsync(message.AsMemory(), ct).ConfigureAwait(false);
            await _proc.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await _proc.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                await _incoming.Writer.WriteAsync(line).ConfigureAwait(false);
            }
        }
        catch
        {
            // 프로세스 종료 시 정상 흐름
        }
        finally
        {
            _incoming.Writer.TryComplete();
        }
    }

    private async Task DrainStdErrAsync()
    {
        try
        {
            while (await _proc.StandardError.ReadLineAsync().ConfigureAwait(false) is not null)
            {
                // 버퍼 막힘 방지를 위해 읽고 버림 (서버 로그)
            }
        }
        catch
        {
            // 무시
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_proc.HasExited)
            {
                _proc.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 무시
        }

        _proc.Dispose();
        await ValueTask.CompletedTask;
    }
}
