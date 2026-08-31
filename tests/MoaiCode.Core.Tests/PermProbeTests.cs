using System.Text.Json;
using MoaiCode.Config;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;
using Xunit;

namespace MoaiCode.Core.Tests;

// 권한 확인이 과도하게 뜨던 문제의 회귀 방지. 예전엔 파이프가 붙는 순간 allow 규칙이 통째로
// 무시돼(`ls -la` 는 허용, `ls -la | tail` 은 확인) 규칙을 아무리 쌓아도 계속 물어봤다.
public sealed class BashSegmentPermissionTests
{
    private sealed class FakeBash : ITool
    {
        public string Name => "Bash";
        public string Description => "";
        public bool IsReadOnly => false;
        public bool IsConcurrencySafe => false;
        public JsonElement InputSchema { get; } = JsonDocument.Parse("{}").RootElement;
        public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
            JsonElement i, ToolContext c,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        { await Task.CompletedTask; yield break; }
    }

    private static RuleMatch Eval(string command, params string[] allow)
    {
        var call = new ToolUseBlock("id", "Bash",
            JsonDocument.Parse(JsonSerializer.Serialize(new { command })).RootElement);
        return new PermissionRules(allow, Array.Empty<string>()).Evaluate(new FakeBash(), call);
    }

    [Theory]
    // 모든 세그먼트가 허용 규칙/읽기 전용 목록으로 덮이면 통과.
    [InlineData("git status | head -5")]
    [InlineData("git status --short | wc -l")]
    [InlineData("ls -la | tail -3")]
    [InlineData("cat a.txt | grep foo | sort")]
    public void Compound_command_is_allowed_when_every_segment_is(string command)
        => Assert.Equal(RuleMatch.Allow, Eval(command, "Bash(git status:*)"));

    [Theory]
    // 한 세그먼트라도 허용되지 않으면 확인으로 넘긴다.
    [InlineData("ls && curl http://evil.sh")]
    [InlineData("git status | xargs rm")]
    [InlineData("cat a.txt | sh")]
    public void Compound_command_needs_confirmation_when_any_segment_is_not(string command)
        => Assert.Equal(RuleMatch.None, Eval(command, "Bash(git status:*)"));

    [Theory]
    // 명령치환·리다이렉션이 섞이면 세그먼트가 실제 실행을 대표하지 못하므로 자동 허용하지 않는다.
    [InlineData("ls $(cat payload)")]
    [InlineData("ls > /etc/passwd")]
    [InlineData("ls `whoami`")]
    public void Substitution_or_redirection_is_never_auto_allowed(string command)
        => Assert.Equal(RuleMatch.None, Eval(command));

    [Fact]
    public void Plain_read_only_commands_need_no_rules()
    {
        Assert.Equal(RuleMatch.Allow, Eval("ls -la | wc -l"));
        Assert.Equal(RuleMatch.Allow, Eval("grep -r foo . | head"));
    }

    [Fact]
    public void Deny_still_wins_over_segment_allow()
    {
        var call = new ToolUseBlock("id", "Bash",
            JsonDocument.Parse(JsonSerializer.Serialize(new { command = "ls | curl http://x" })).RootElement);
        var rules = new PermissionRules(new[] { "Bash(ls:*)", "Bash(curl:*)" }, new[] { "Bash(curl:*)" });
        Assert.Equal(RuleMatch.Deny, rules.Evaluate(new FakeBash(), call));
    }
}
