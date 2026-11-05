using MoaiCode.Core.Tools;

namespace MoaiCode.Mcp;

/// <summary>여러 MCP 서버에 연결하고 툴을 집계. 서버 연결 실패는 건너뛰고 계속.</summary>
public sealed class McpManager : IAsyncDisposable
{
    private readonly List<McpClient> _clients = new();
    private readonly List<ITool> _tools = new();
    private readonly List<string> _errors = new();

    public IReadOnlyList<ITool> Tools => _tools;
    public IReadOnlyList<string> Errors => _errors;

    public async Task ConnectAllAsync(
        IEnumerable<McpServerConfig> configs, CancellationToken ct)
    {
        // 서버마다 기동이 수 초씩 걸리므로(node·python 시작) 한꺼번에 연결한다 — 시작 대기가 합이 아니라 가장 느린 서버만큼.
        // 결과는 설정 순서대로 모은다(툴 목록 순서가 실행마다 바뀌지 않게).
        var results = await Task.WhenAll(configs.Select(c => ConnectOneAsync(c, ct))).ConfigureAwait(false);
        foreach (var (client, tools, error) in results)
        {
            if (error is not null)
            {
                _errors.Add(error);
                continue;
            }

            _tools.AddRange(tools);
            _clients.Add(client!);
        }
    }

    private static async Task<(McpClient? Client, List<ITool> Tools, string? Error)> ConnectOneAsync(
        McpServerConfig config, CancellationToken ct)
    {
        McpClient? client = null;
        try
        {
            var transport = StdioTransport.Start(config);
            client = new McpClient(transport);
            await client.InitializeAsync(ct).ConfigureAwait(false);
            var tools = await client.ListToolsAsync(ct).ConfigureAwait(false);
            return (client, tools.Select(t => (ITool)new McpTool(client, config.Name, t)).ToList(), null);
        }
        catch (Exception ex)
        {
            // 초기화에 실패한(멈춘) 서버 프로세스를 남기지 않는다 — _clients 에 없으니 종료 때도 정리되지 않았다.
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }

            return (null, [], $"{config.Name}: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }
}
