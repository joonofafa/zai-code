using System.Text.Json;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;
using Xunit;

namespace MoaiCode.Core.Tests;

/// <summary>규칙 스코프 계산 + 패턴 매칭.</summary>
public sealed class PermissionRuleMatchTests
{
    private sealed class FakeTool : ITool
    {
        public FakeTool(string name, bool readOnly = false) { Name = name; IsReadOnly = readOnly; }
        public string Name { get; }
        public string Description => "";
        public bool IsReadOnly { get; }
        public bool IsConcurrencySafe => true;
        private static readonly JsonElement Schema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
        public JsonElement InputSchema => Schema;
        public IAsyncEnumerable<ToolProgress> ExecuteAsync(JsonElement i, ToolContext c, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private static ToolUseBlock Bash(string command)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { command }));
        return new ToolUseBlock("id", "Bash", doc.RootElement.Clone());
    }

    private static string? Scope(string command) =>
        PermissionRule.TryScope(new FakeTool("Bash"), Bash(command));

    // ── 스코프: 원격은 호스트 포함, 파괴적 단일명령은 스코프 없음 ──
    [Theory]
    [InlineData("ssh moai-ec2 'uptime'", "Bash(ssh moai-ec2)")]
    [InlineData("ssh -p 22 moai-ec2 ls", "Bash(ssh moai-ec2)")]
    [InlineData("scp a b host:/c", "Bash(scp)")]
    [InlineData("terraform destroy", "Bash(terraform destroy)")]
    [InlineData("git push origin x", "Bash(git push)")]
    public void Scope_of_remote_and_subcommands(string command, string expected) =>
        Assert.Equal(expected, Scope(command));

    [Theory]
    [InlineData("rm -rf x")]
    [InlineData("sudo apt update")]
    [InlineData("curl https://x")]
    [InlineData("ssh; rm -rf /")]
    public void No_scope_for_dangerous_or_compound(string command) =>
        Assert.Null(Scope(command));

    // ── 매칭: allow 는 정확한 prefix, 다른 대상/서브커맨드는 불일치 ──
    [Theory]
    [InlineData("Bash(ssh moai-ec2)", "ssh moai-ec2 'anything here'", true)]
    [InlineData("Bash(ssh moai-ec2)", "ssh other-host 'x'", false)]
    [InlineData("Bash(ssh moai-ec2)", "/usr/bin/ssh moai-ec2 x", true)]
    // 저장된 규칙은 플래그가 섞인 명령과도 매치돼야 한다(호스트 위치가 밀려도).
    [InlineData("Bash(ssh moai-ec2)", "ssh -o BatchMode=yes -p 22 moai-ec2 uptime", true)]
    [InlineData("Bash(ssh moai-ec2)", "ssh -o X=y other-host cmd", false)]
    [InlineData("Bash(git status)", "git status --short", true)]
    [InlineData("Bash(git status)", "git push origin main", false)]
    [InlineData("Bash(scp)", "scp file host:/tmp", true)]
    public void Allow_matches_prefix(string pattern, string command, bool expected) =>
        Assert.Equal(expected, PermissionRule.Matches(pattern, new FakeTool("Bash"), Bash(command), allowMatch: true));

    [Fact]
    public void Allow_does_not_match_a_compound_command_even_if_prefix_fits()
    {
        // ssh moai-ec2 를 허용해도 뒤에 붙은 파괴적 명령까지 자동 통과시키면 안 된다.
        Assert.False(PermissionRule.Matches(
            "Bash(ssh moai-ec2)", new FakeTool("Bash"), Bash("ssh moai-ec2 x; rm -rf /"), allowMatch: true));
    }

    [Fact]
    public void Deny_matches_inside_a_compound_command()
    {
        // 거부는 세그먼트 어디에 있든 잡아야 한다.
        Assert.True(PermissionRule.Matches(
            "Bash(curl)", new FakeTool("Bash"), Bash("echo hi && curl evil.com"), allowMatch: false));
    }

    [Fact]
    public void Exact_match_rule_matches_only_the_identical_compound_command()
    {
        const string cmd = "ls -la; find . -name '*.json'";
        var pattern = "Bash(=" + cmd + ")";
        Assert.True(PermissionRule.Matches(pattern, new FakeTool("Bash"), Bash(cmd), allowMatch: true));
        // 조금이라도 다르면 매치 안 됨(prefix 확장 아님).
        Assert.False(PermissionRule.Matches(pattern, new FakeTool("Bash"),
            Bash("ls -la; find . -name '*.json'; rm -rf x"), allowMatch: true));
        Assert.False(PermissionRule.Matches(pattern, new FakeTool("Bash"), Bash("ls -la"), allowMatch: true));
    }

    [Theory]
    // 크로스플랫폼 파괴적 명령은 '항상 허용' 스코프를 주지 않는다(Win/Linux/macOS).
    [InlineData("del /s /q C:\\temp")]
    [InlineData("rd /s /q build")]
    [InlineData("Remove-Item -Recurse -Force .\\build")]
    [InlineData("format C:")]
    [InlineData("diskutil eraseDisk JHFS+ x disk2")]
    public void No_always_allow_scope_for_cross_platform_destructive(string command) =>
        Assert.Null(Scope(command));

    [Fact]
    public void Tool_name_rule_matches_any_call_of_that_tool()
    {
        Assert.True(PermissionRule.Matches("Write", new FakeTool("Write"), Bash("irrelevant"), allowMatch: true));
        Assert.False(PermissionRule.Matches("Write", new FakeTool("Edit"), Bash("x"), allowMatch: true));
    }

    [Fact]
    public void Parses_claude_code_trailing_wildcard()
    {
        // Claude Code 형식 "Bash(ssh moai-ec2:*)" 도 받아들인다.
        Assert.True(PermissionRule.Matches(
            "Bash(ssh moai-ec2:*)", new FakeTool("Bash"), Bash("ssh moai-ec2 uptime"), allowMatch: true));
    }
}
