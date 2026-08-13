using System.Runtime.CompilerServices;
using System.Text.Json;
using MoaiCode.Core.Agent.Prompts;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;

namespace MoaiCode.Mcp;

/// <summary>MCP 서버 툴을 ITool로 래핑. 이름은 mcp__{server}__{tool} (충돌 방지).</summary>
public sealed class McpTool : ITool
{
    private readonly McpClient _client;
    private readonly McpToolInfo _info;

    public McpTool(McpClient client, string serverName, McpToolInfo info)
    {
        _client = client;
        _info = info;
        Name = $"mcp__{serverName}__{info.Name}";
    }

    public string Name { get; }
    public string Description => _info.Description;
    public JsonElement InputSchema => _info.InputSchema;
    public bool IsReadOnly => false;
    public bool IsConcurrencySafe => false;

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        McpCallResult result;
        Exception? error = null;
        try
        {
            result = await _client.CallToolAsync(_info.Name, input, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            error = ex;
            result = null!;
        }

        if (error is not null)
        {
            yield return new ToolOutput(L10n.Get("mcp.callFailed", error.Message), IsError: true);
            yield break;
        }

        // 외부(MCP) 결과는 신뢰불가 데이터 — 간접 프롬프트 인젝션 경계를 앞에 붙인다.
        yield return new ToolOutput(
            string.IsNullOrEmpty(result.Text)
                ? "(no content)"
                : Reminders.UntrustedToolOutput + result.Text,
            IsError: result.IsError);
    }
}
