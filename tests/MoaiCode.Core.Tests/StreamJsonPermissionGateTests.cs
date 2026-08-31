using System.Text.Json;
using System.Text.Json.Nodes;
using MoaiCode.Cli;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;
using MoaiCode.Tui.Commands;
using Xunit;

namespace MoaiCode.Core.Tests;

// stream-json 승인 왕복: permission_response 라우팅과 StreamJsonPermissionGate 의 허용/거부/타임아웃.
// 게이트의 stdout 출력은 emit 콜백으로 캡처한다(콘솔 오염 방지).
public sealed class StreamJsonPermissionGateTests
{
    private static (StreamJsonPermissionGate Gate, List<JsonObject> Events) Make(Action<string>? persist = null)
    {
        var events = new List<JsonObject>();
        var gate = new StreamJsonPermissionGate(evt => events.Add(evt), persist);
        return (gate, events);
    }

    private static ToolUseBlock BashCall(string command)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { command }));
        return new ToolUseBlock("toolu_1", "Bash", doc.RootElement.Clone());
    }

    [Fact]
    public async Task Allow_response_resolves_pending_request()
    {
        var (gate, events) = Make();
        var pending = gate.AllowAsync(new FakeTool(), BashCall("ssh moai-ec2 uptime"), CancellationToken.None);

        Assert.Single(events, e => e["type"]!.GetValue<string>() == "permission_request");
        var id = events.First(e => e["type"]!.GetValue<string>() == "permission_request")["request_id"]!.GetValue<string>();

        gate.HandleResponse(id, "allow");
        Assert.True(await pending);
    }

    [Fact]
    public async Task Deny_response_rejects_pending_request()
    {
        var (gate, events) = Make();
        var pending = gate.AllowAsync(new FakeTool(), BashCall("ssh moai-ec2 uptime"), CancellationToken.None);
        var id = events.First(e => e["type"]!.GetValue<string>() == "permission_request")["request_id"]!.GetValue<string>();

        gate.HandleResponse(id, "deny");
        Assert.False(await pending);
    }

    [Fact]
    public async Task Unknown_request_id_is_ignored()
    {
        var (gate, _) = Make();
        gate.HandleResponse("no-such-id", "allow");   // 예외 없이 무시
        await Task.Delay(10, CancellationToken.None); // 완료 대기 없음 — 죽지 않는지만 확인
    }

    [Fact]
    public async Task Allow_always_persists_scope_and_allows()
    {
        var saved = new List<string>();
        var (gate, events) = Make(saved.Add);
        var pending = gate.AllowAsync(new FakeTool(), BashCall("ssh moai-ec2 uptime"), CancellationToken.None);

        var req = events.First(e => e["type"]!.GetValue<string>() == "permission_request");
        Assert.NotNull(req["scope"]);   // ssh 는 host 스코프를 제공한다
        gate.HandleResponse(req["request_id"]!.GetValue<string>(), "allow_always");

        Assert.True(await pending);
        Assert.Single(saved, s => s == req["scope"]!.GetValue<string>());
    }

    [Fact]
    public async Task Timeout_emits_result_and_denies()
    {
        Environment.SetEnvironmentVariable("MOAI_PERMISSION_TIMEOUT", "1");
        try
        {
            var (gate, events) = Make();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var allowed = await gate.AllowAsync(new FakeTool(), BashCall("ssh moai-ec2 uptime"), CancellationToken.None);
            sw.Stop();

            Assert.False(allowed);
            Assert.Contains(events, e =>
                e["type"]!.GetValue<string>() == "permission_result"
                && e["outcome"]!.GetValue<string>() == "timeout");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MOAI_PERMISSION_TIMEOUT", null);
        }
    }

    [Fact]
    public async Task FailAllPending_rejects_waiting_requests()
    {
        var (gate, _) = Make();
        var pending = gate.AllowAsync(new FakeTool(), BashCall("ssh moai-ec2 uptime"), CancellationToken.None).AsTask();
        gate.FailAllPending();   // stdin EOF 시나리오
        Assert.False(await pending);
    }

    [Fact]
    public async Task Cancellation_rejects_pending_request_and_emits_canceled_result()
    {
        var (gate, events) = Make();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var allowed = await gate.AllowAsync(
            new FakeTool(), BashCall("ssh moai-ec2 uptime"), cts.Token);

        Assert.False(allowed);
        Assert.Contains(events, e =>
            e["type"]!.GetValue<string>() == "permission_result"
            && e["outcome"]!.GetValue<string>() == "canceled");
    }

    [Fact]
    public async Task ModeAware_gate_uses_stream_gate_for_regular_ask_permissions()
    {
        var (streamGate, events) = Make();
        var modeGate = new ModeAwarePermissionGate(
            new AgentRuntimeState(), streamGate, Path.GetTempPath(), confine: false,
            confirmer: streamGate);
        using var doc = JsonDocument.Parse("{}");
        var call = new ToolUseBlock("toolu_write", "Write", doc.RootElement.Clone());
        var pending = modeGate.AllowAsync(new FakeTool("Write"), call, CancellationToken.None).AsTask();

        var request = Assert.Single(events, e =>
            e["type"]!.GetValue<string>() == "permission_request");
        streamGate.HandleResponse(request["request_id"]!.GetValue<string>(), "allow");

        Assert.True(await pending);
    }

    [Fact]
    public void Routing_detects_permission_response_lines()
    {
        var (gate, _) = Make();
        var response = """{"type":"permission_response","request_id":"abc","decision":"allow"}""";
        var userTurn = """{"type":"user","message":{"content":[{"type":"text","text":"hi"}]}}""";

        Assert.True(StreamJsonRunner.TryRoutePermissionResponse(response, gate));
        Assert.False(StreamJsonRunner.TryRoutePermissionResponse(userTurn, gate));
        Assert.False(StreamJsonRunner.TryRoutePermissionResponse("not-json", gate));
        Assert.False(StreamJsonRunner.TryRoutePermissionResponse("[]", gate));
        Assert.False(StreamJsonRunner.TryRoutePermissionResponse(response, null));   // 게이트 없으면 라우팅 안 함
    }

    private sealed class FakeTool : ITool
    {
        public FakeTool(string name = "Bash") => Name = name;

        public string Name { get; }
        public string Description => "fake";
        public bool IsReadOnly => false;
        public bool IsConcurrencySafe => false;
        private static readonly JsonElement Schema = JsonDocument.Parse("{}").RootElement.Clone();
        public JsonElement InputSchema => Schema;
        public IAsyncEnumerable<ToolProgress> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
