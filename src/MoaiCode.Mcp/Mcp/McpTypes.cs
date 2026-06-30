using System.Text.Json;

namespace MoaiCode.Mcp;

/// <summary>MCP 서버 구동 설정 (.mcp.json의 mcpServers 항목).</summary>
public sealed record McpServerConfig(
    string Name,
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Env);

/// <summary>tools/list가 반환하는 툴 메타데이터.</summary>
public sealed record McpToolInfo(string Name, string Description, JsonElement InputSchema);

/// <summary>tools/call 결과 (content 텍스트 결합 + isError).</summary>
public sealed record McpCallResult(string Text, bool IsError);

/// <summary>MCP 프로토콜/JSON-RPC 오류.</summary>
public sealed class McpException(string message) : Exception(message);
