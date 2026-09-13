using System.Net;
using System.Text.Json;
using MoaiCode.Config;
using MoaiCode.Core.Agent;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;
using MoaiCode.Providers;
using MoaiCode.Tools.Agent;
using Xunit;

namespace MoaiCode.Core.Tests;

/// <summary>보안 강화(SE1~SE4) 회귀 테스트.</summary>
public class SecurityHardeningTests
{
    // ── SE3: UDP 로그 대상은 사설/루프백 대역만 ──
    // ConfigureUdp 은 private 상태를 바꾸므로, 활성/비활성 여부를 리플렉션으로 검증한다.
    private static bool IsUdpActive()
    {
        var f = typeof(MoaiLog).GetField("_udp", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return f?.GetValue(null) is not null;
    }

    [Theory]
    [InlineData("127.0.0.1")]           // loopback
    [InlineData("10.0.0.5")]            // RFC1918 10/8
    [InlineData("192.168.1.10")]        // RFC1918 192.168/16
    [InlineData("172.16.0.1")]          // RFC1918 172.16/12
    public void Udp_target_private_network_is_allowed(string host)
    {
        MoaiLog.ConfigureUdp(host + ":5599");
        Assert.True(IsUdpActive(), $"{host} should be allowed");
        MoaiLog.ConfigureUdp(null);
    }

    [Theory]
    [InlineData("8.8.8.8")]             // 공인 DNS
    [InlineData("1.1.1.1")]             // 공인
    [InlineData("203.0.113.5")]         // TEST-NET-3 (공인 문서 대역)
    public void Udp_target_public_network_is_rejected(string host)
    {
        MoaiLog.ConfigureUdp(host + ":5599");
        Assert.False(IsUdpActive(), $"{host} must be rejected (plaintext UDP would leak off-LAN)");
        MoaiLog.ConfigureUdp(null);
    }

    // ── SE4: AgentTool 게이트 미주입 시 fail-closed ──
    private sealed class WriteRequestingModel : IChatModel
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            IReadOnlyList<Message> messages,
            IReadOnlyList<ITool> tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            using var doc = JsonDocument.Parse("""{"path":"pwn.txt","content":"x"}""");
            yield return new ToolCallRequested(new ToolUseBlock("c1", "Write", doc.RootElement.Clone()));
            yield return new TurnCompleted(new Usage(1, 1), "tool_calls");
        }
    }

    private sealed class RecordingWriteTool : ITool
    {
        public bool Executed;
        public string Name => "Write";
        public string Description => "test";
        public bool IsReadOnly => false;
        public bool IsConcurrencySafe => true;
        public JsonElement InputSchema { get; } = Parse("""{"type":"object"}""");

        private static JsonElement Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }

        public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
            JsonElement input, ToolContext context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            Executed = true;
            yield return new ToolOutput("written");
        }
    }

    [Fact]
    public async Task Agent_without_gate_denies_writes()
    {
        // 게이트 없이 spawn된 서브에이전트는 쓰기 툴을 실행할 수 없어야 한다(fail-closed).
        var sub = new RecordingWriteTool();
        var agent = new AgentTool(new WriteRequestingModel(), new ITool[] { sub });

        var sb = new System.Text.StringBuilder();
        using var doc = JsonDocument.Parse("""{"prompt":"write the file"}""");
        await foreach (var p in agent.ExecuteAsync(doc.RootElement, new ToolContext(".", PermissionMode.Auto), default))
        {
            if (p is ToolOutput o)
            {
                sb.Append(o.Text);
            }
        }

        Assert.False(sub.Executed, "sub-agent must not execute write tools when no gate is injected");
    }
}
