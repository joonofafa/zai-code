using System.Text.Json;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Security;
using MoaiCode.Core.Tools;
using Xunit;

namespace MoaiCode.Core.Tests;

/// <summary>
/// "항상 허용" 스코프 회귀 테스트. 예전엔 툴 이름으로만 키를 잡아, `ls` 한 번 항상 허용하면
/// 그 세션의 모든 Bash 명령이 무조건 승인됐다.
/// </summary>
public sealed class PermissionScopeTests
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

    private static string? Scope(string toolName, string? command)
    {
        var json = command is null ? "{}" : JsonSerializer.Serialize(new { command });
        using var doc = JsonDocument.Parse(json);
        return PermissionRule.TryScope(new FakeTool(toolName), new ToolUseBlock("id", toolName, doc.RootElement.Clone()));
    }

    [Fact]
    public void Non_bash_tools_scope_to_the_tool_name()
    {
        Assert.Equal("Write", Scope("Write", null));
    }

    [Theory]
    [InlineData("git status", "Bash(git status)")]
    [InlineData("git push origin main", "Bash(git push)")]
    [InlineData("ls -la", "Bash(ls)")]
    [InlineData("npm run build", "Bash(npm run)")]
    [InlineData("/usr/bin/dotnet test", "Bash(dotnet test)")]
    [InlineData("dotnet --version", "Bash(dotnet)")]
    public void Bash_scopes_to_a_command_prefix(string command, string expected)
    {
        Assert.Equal(expected, Scope("Bash", command));
    }

    [Theory]
    // 복합 명령은 첫 토큰이 명령 전체를 대표하지 못한다 → 항상 허용 금지.
    [InlineData("git status && rm -rf /tmp/x")]
    [InlineData("ls; rm -rf build")]
    [InlineData("ls | xargs rm")]
    [InlineData("echo $(rm -rf x)")]
    [InlineData("cat f > /etc/passwd")]
    [InlineData("ls `rm -rf x`")]
    public void Compound_commands_get_no_always_allow_scope(string command)
    {
        Assert.Null(Scope("Bash", command));
    }

    [Theory]
    // 프롬프트 "항상 허용"으로는 넓히지 않는 명령(파괴적 단일명령/권한상승/네트워크 egress).
    // ssh 는 호스트 단위로 스코프 가능하므로 여기서 제외(PermissionRuleMatchTests 참고).
    [InlineData("rm -rf build")]
    [InlineData("sudo apt install x")]
    [InlineData("curl https://example.com")]
    [InlineData("chmod 777 f")]
    public void Dangerous_commands_get_no_always_allow_scope(string command)
    {
        Assert.Null(Scope("Bash", command));
    }

    [Fact]
    public void Env_prefixed_commands_get_no_scope()
    {
        Assert.Null(Scope("Bash", "FOO=bar somecmd"));
    }
}

/// <summary>LLM 분류기 응답 파싱 — 모델이 코드펜스/잡담을 섞어도 판정을 뽑아내야 한다.</summary>
public sealed class RiskVerdictParseTests
{
    [Fact]
    public void Parses_plain_json()
    {
        var v = LlmRiskClassifier.Parse("""{"decision":"deny","reason":"not requested"}""");
        Assert.Equal(RiskDecision.Deny, v!.Decision);
        Assert.Equal("not requested", v.Reason);
    }

    [Fact]
    public void Parses_json_wrapped_in_a_code_fence()
    {
        var v = LlmRiskClassifier.Parse("```json\n{\"decision\":\"confirm\",\"reason\":\"destructive\"}\n```");
        Assert.Equal(RiskDecision.Confirm, v!.Decision);
    }

    [Fact]
    public void Parses_allow()
    {
        Assert.Equal(RiskDecision.Allow, LlmRiskClassifier.Parse("""{"decision":"ALLOW","reason":""}""")!.Decision);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sorry, I cannot help")]
    [InlineData("{\"decision\":\"maybe\"}")]
    [InlineData("{not json")]
    [InlineData("{\"reason\":\"no decision field\"}")]
    public void Returns_null_when_unparseable(string text)
    {
        // null → 호출측이 fail-closed 로 처리한다.
        Assert.Null(LlmRiskClassifier.Parse(text));
    }
}
