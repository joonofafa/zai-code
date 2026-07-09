using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MoaiCode.Mcp;

/// <summary>
/// MCP JSON-RPC 2.0 클라이언트 (transport 비의존). initialize → tools/list → tools/call.
/// 응답은 id 매칭으로 pending TCS에 디스패치 (백그라운드 펌프).
/// </summary>
public sealed class McpClient : IAsyncDisposable
{
    private const string ProtocolVersion = "2024-11-05";

    private readonly IMcpTransport _transport;
    private readonly Dictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private long _nextId;

    public McpClient(IMcpTransport transport)
    {
        _transport = transport;
        _pump = Task.Run(PumpAsync);
    }

    // 하드코딩하면 릴리스 때마다 어긋난다(실제로 1.1.2 로 굳어 있었다).
    private static string ClientVersion =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "0.0.0";

    public async Task InitializeAsync(CancellationToken ct)
    {
        var p = new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "moai-code", ["version"] = ClientVersion },
        };

        await RequestAsync("initialize", p, ct).ConfigureAwait(false);
        await NotifyAsync("notifications/initialized", null, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct)
    {
        var result = await RequestAsync("tools/list", null, ct).ConfigureAwait(false);
        var list = new List<McpToolInfo>();
        if (result.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tools.EnumerateArray())
            {
                var name = t.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (name.Length == 0)
                {
                    continue;
                }

                var desc = t.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                var schema = t.TryGetProperty("inputSchema", out var s) && s.ValueKind == JsonValueKind.Object
                    ? s.Clone()
                    : EmptySchema();

                list.Add(new McpToolInfo(name, desc, schema));
            }
        }

        return list;
    }

    public async Task<McpCallResult> CallToolAsync(string name, JsonElement arguments, CancellationToken ct)
    {
        var argsNode = arguments.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(arguments.GetRawText())
            : new JsonObject();

        var p = new JsonObject { ["name"] = name, ["arguments"] = argsNode };
        var result = await RequestAsync("tools/call", p, ct).ConfigureAwait(false);

        var sb = new StringBuilder();
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                {
                    sb.AppendLine(txt.GetString());
                }
            }
        }

        var isError = result.TryGetProperty("isError", out var e)
                      && e.ValueKind is JsonValueKind.True;

        return new McpCallResult(sb.ToString().TrimEnd(), isError);
    }

    // MCP 서버가 응답하지 않을 때 무한 대기를 막는 요청 타임아웃 (행 서버로 인한 턴 멈춤 방지).
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);

    private async Task<JsonElement> RequestAsync(string method, JsonNode? param, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _pending[id] = tcs;
        }

        var msg = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (param is not null)
        {
            msg["params"] = param;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(RequestTimeout);
        try
        {
            await _transport.SendAsync(msg.ToJsonString(), timeoutCts.Token).ConfigureAwait(false);

            await using (timeoutCts.Token.Register(() => tcs.TrySetCanceled(timeoutCts.Token)))
            {
                try
                {
                    return await tcs.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    // 사용자 취소(ct)가 아니라 타임아웃 → 일반 오류로 변환(McpTool이 에러 출력으로 처리).
                    throw new TimeoutException(
                        $"MCP request '{method}' timed out after {RequestTimeout.TotalSeconds:0}s");
                }
            }
        }
        finally
        {
            lock (_lock)
            {
                _pending.Remove(id); // 타임아웃/취소 시 누수 방지
            }
        }
    }

    private async Task NotifyAsync(string method, JsonNode? param, CancellationToken ct)
    {
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (param is not null)
        {
            msg["params"] = param;
        }

        await _transport.SendAsync(msg.ToJsonString(), ct).ConfigureAwait(false);
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var line in _transport.Incoming.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    continue;
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                    {
                        continue; // 서버 발신 notification/request는 스켈레톤에서 무시
                    }

                    var id = idEl.GetInt64();
                    TaskCompletionSource<JsonElement>? tcs;
                    lock (_lock)
                    {
                        _pending.Remove(id, out tcs);
                    }

                    if (tcs is null)
                    {
                        continue;
                    }

                    if (root.TryGetProperty("error", out var err))
                    {
                        tcs.TrySetException(new McpException($"MCP error: {err.GetRawText()}"));
                    }
                    else if (root.TryGetProperty("result", out var res))
                    {
                        tcs.TrySetResult(res.Clone());
                    }
                    else
                    {
                        tcs.TrySetResult(EmptySchema());
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // dispose
        }
    }

    private static JsonElement EmptySchema()
    {
        using var d = JsonDocument.Parse("""{"type":"object"}""");
        return d.RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch
        {
            // 무시
        }

        _cts.Dispose();
    }
}
