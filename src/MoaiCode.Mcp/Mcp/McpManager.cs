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
        foreach (var config in configs)
        {
            try
            {
                var transport = StdioTransport.Start(config);
                var client = new McpClient(transport);
                await client.InitializeAsync(ct).ConfigureAwait(false);
                var tools = await client.ListToolsAsync(ct).ConfigureAwait(false);
                foreach (var t in tools)
                {
                    _tools.Add(new McpTool(client, config.Name, t));
                }

                _clients.Add(client);
            }
            catch (Exception ex)
            {
                _errors.Add($"{config.Name}: {ex.Message}");
            }
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
