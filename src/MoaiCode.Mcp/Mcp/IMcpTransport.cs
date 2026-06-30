using System.Threading.Channels;

namespace MoaiCode.Mcp;

/// <summary>
/// MCP 메시지 전송 추상화 (newline-delimited JSON). 실제는 stdio(StdioTransport),
/// 테스트는 인메모리 페이크로 교체 가능 → JSON-RPC 로직을 프로세스 없이 검증.
/// </summary>
public interface IMcpTransport : IAsyncDisposable
{
    Task SendAsync(string message, CancellationToken ct);

    ChannelReader<string> Incoming { get; }
}
