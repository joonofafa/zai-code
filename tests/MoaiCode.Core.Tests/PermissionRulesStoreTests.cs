using System.Text.Json;
using MoaiCode.Config;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;
using Xunit;

namespace MoaiCode.Core.Tests;

/// <summary>규칙 저장소: 평가(deny&gt;allow), settings.json 저장/로드 왕복.</summary>
public sealed class PermissionRulesStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public PermissionRulesStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "moai-rules-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best-effort */ }
    }

    private sealed class FakeTool : ITool
    {
        public FakeTool(string name) => Name = name;
        public string Name { get; }
        public string Description => "";
        public bool IsReadOnly => false;
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

    [Fact]
    public void Evaluate_allow_and_none()
    {
        var r = new PermissionRules(allow: new[] { "Bash(ssh moai-ec2)" });
        Assert.Equal(RuleMatch.Allow, r.Evaluate(new FakeTool("Bash"), Bash("ssh moai-ec2 uptime")));
        Assert.Equal(RuleMatch.None, r.Evaluate(new FakeTool("Bash"), Bash("ssh other uptime")));
    }

    [Fact]
    public void Deny_beats_allow()
    {
        var r = new PermissionRules(allow: new[] { "Bash(curl)" }, deny: new[] { "Bash(curl)" });
        Assert.Equal(RuleMatch.Deny, r.Evaluate(new FakeTool("Bash"), Bash("curl https://x")));
    }

    [Fact]
    public void Add_persists_and_reloads_from_settings()
    {
        var r = new PermissionRules(path: _path);
        r.AddAllow("Bash(ssh moai-ec2)");
        r.AddDeny("Bash(curl)");

        Assert.True(File.Exists(_path));

        // 저장된 파일을 SettingsLoader 로 다시 읽어 규칙이 살아있는지 확인.
        var loaded = SettingsLoader.ApplyJson(Settings.Default, File.ReadAllText(_path));
        Assert.Contains("Bash(ssh moai-ec2)", loaded.AllowRules);
        Assert.Contains("Bash(curl)", loaded.DenyRules);

        var r2 = new PermissionRules(loaded.AllowRules, loaded.DenyRules, _path);
        Assert.Equal(RuleMatch.Allow, r2.Evaluate(new FakeTool("Bash"), Bash("ssh moai-ec2 x")));
        Assert.Equal(RuleMatch.Deny, r2.Evaluate(new FakeTool("Bash"), Bash("curl x")));
    }

    [Fact]
    public void Add_is_idempotent()
    {
        var r = new PermissionRules(path: _path);
        r.AddAllow("Bash(git status)");
        r.AddAllow("Bash(git status)");
        Assert.Single(r.Allow);
    }

    [Fact]
    public void Remove_deletes_from_both_lists()
    {
        var r = new PermissionRules(allow: new[] { "Bash(a)" }, deny: new[] { "Bash(b)" }, path: _path);
        Assert.True(r.Remove("Bash(a)"));
        Assert.True(r.Remove("Bash(b)"));
        Assert.False(r.Remove("Bash(nonexistent)"));
        Assert.Empty(r.Allow);
        Assert.Empty(r.Deny);
    }

    [Fact]
    public void SettingsWriter_preserves_other_keys()
    {
        File.WriteAllText(_path, """{"model":"gpt-4o","permission":"ask"}""");
        var r = new PermissionRules(path: _path);
        r.AddAllow("Bash(ls)");

        var loaded = SettingsLoader.ApplyJson(Settings.Default, File.ReadAllText(_path));
        Assert.Equal("gpt-4o", loaded.Model);       // 기존 키 보존
        Assert.Contains("Bash(ls)", loaded.AllowRules);
    }

    [Fact]
    public void Loader_reads_top_level_and_nested_forms()
    {
        var nested = SettingsLoader.ApplyJson(Settings.Default,
            """{"permissions":{"allow":["Bash(ls)"],"deny":["Bash(rm)"]}}""");
        Assert.Contains("Bash(ls)", nested.AllowRules);
        Assert.Contains("Bash(rm)", nested.DenyRules);
    }

    [Fact]
    public void Loader_merges_rules_across_layers()
    {
        var layer1 = SettingsLoader.ApplyJson(Settings.Default, """{"permissions":{"allow":["Bash(a)"]}}""");
        var layer2 = SettingsLoader.ApplyJson(layer1, """{"permissions":{"allow":["Bash(b)"]}}""");
        Assert.Contains("Bash(a)", layer2.AllowRules);
        Assert.Contains("Bash(b)", layer2.AllowRules);
    }
}
