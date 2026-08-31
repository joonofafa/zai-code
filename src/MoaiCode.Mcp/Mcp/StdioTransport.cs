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

        // 보안(SEC-001): 자식 MCP 프로세스는 부모 환경을 상속한다. 로그인 API 키·프록시 비번 등 민감
        // 자격증명이 그대로 넘어가면 악성 MCP 서버가 읽어갈 수 있으므로, 상속된 민감 변수는 제거한다.
        // 서버가 정말 필요로 하는 값은 config 의 env 에 명시하게 하고(아래에서 다시 주입), 그것만 전달한다.
        foreach (var key in psi.Environment.Keys.Where(IsSensitiveEnvKey).ToList())
        {
            psi.Environment.Remove(key);
        }

        foreach (var (k, v) in config.Env)
        {
            psi.Environment[k] = v;
        }

        var proc = Process.Start(psi)
                   ?? throw new McpException($"Failed to start MCP server: {config.Command}");
        return new StdioTransport(proc);
    }

    /// <summary>자식 프로세스에 넘기면 안 되는 민감 환경변수 키인지. (상속된 자격증명 유출 차단 · 테스트 대상)</summary>
    public static bool IsSensitiveEnvKey(string key)
    {
        var k = key.ToUpperInvariant();
        return k.StartsWith("MOAI_", StringComparison.Ordinal)          // moai 내부(로그인 호스트 등)
            || k.StartsWith("ZAI_", StringComparison.Ordinal)            // ZAI_API_KEY / ZAI_BASE_URL
            || k.StartsWith("OPENAI_", StringComparison.Ordinal)         // legacy credentials
            || k.StartsWith("ANTHROPIC", StringComparison.Ordinal)
            || k.Contains("API_KEY", StringComparison.Ordinal)
            || k.Contains("SECRET", StringComparison.Ordinal)
            || k.Contains("TOKEN", StringComparison.Ordinal)
            || k.Contains("PASSWORD", StringComparison.Ordinal)
            || k.Contains("CREDENTIAL", StringComparison.Ordinal);
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
