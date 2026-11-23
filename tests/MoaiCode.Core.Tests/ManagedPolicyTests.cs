using MoaiCode.Config;
using Xunit;

namespace MoaiCode.Core.Tests;

/// <summary>중앙 정책 파일 — 여기 적힌 값은 사용자 설정·환경변수로 풀 수 없다.</summary>
[Collection("EnvMutating")]
public sealed class ManagedPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mate-managed-" + Guid.NewGuid().ToString("n"));
    private readonly string _previousPath = SettingsLoader.ManagedSettingsPath;
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("MOAI_HOME");
    private readonly string? _previousSandbox = Environment.GetEnvironmentVariable("MOAI_SANDBOX");
    private readonly string? _previousOffline = Environment.GetEnvironmentVariable("MOAI_OFFLINE");

    public ManagedPolicyTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "home"));
        Directory.CreateDirectory(Path.Combine(_root, "ws"));
        Environment.SetEnvironmentVariable("MOAI_HOME", Path.Combine(_root, "home"));
        SettingsLoader.ManagedSettingsPath = Path.Combine(_root, "managed-settings.json");
    }

    public void Dispose()
    {
        SettingsLoader.ManagedSettingsPath = _previousPath;
        Environment.SetEnvironmentVariable("MOAI_HOME", _previousHome);
        Environment.SetEnvironmentVariable("MOAI_SANDBOX", _previousSandbox);
        Environment.SetEnvironmentVariable("MOAI_OFFLINE", _previousOffline);
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Load_managedFile_winsOverUserSettingsAndEnvironment()
    {
        File.WriteAllText(Path.Combine(_root, "home", "settings.json"),
            """{"sandbox":"off","offline":false,"allow":["Bash(make:*)"],"auditRetentionDays":1}""");
        Environment.SetEnvironmentVariable("MOAI_SANDBOX", "off");
        Environment.SetEnvironmentVariable("MOAI_OFFLINE", "0");
        File.WriteAllText(SettingsLoader.ManagedSettingsPath,
            """
            {
              // central policy
              "sandbox": "required", "offline": true, "auditRetentionDays": 365,
              "deny": ["Bash(curl:*)"], "disableIgnoreConfirm": true
            }
            """);

        var s = SettingsLoader.Load(Path.Combine(_root, "ws"));

        Assert.True(s.ManagedPolicy);
        Assert.Equal("required", s.Sandbox);
        Assert.True(s.Offline);
        Assert.Equal(365, s.AuditRetentionDays);
        Assert.True(s.DisableIgnoreConfirm);
        Assert.Contains("Bash(curl:*)", s.DenyRules);
        Assert.Contains("Bash(make:*)", s.AllowRules);   // 중앙 파일이 말하지 않은 것은 사용자 몫으로 남는다
    }

    [Fact]
    public void Load_withoutManagedFile_keepsUserSettings()
    {
        File.WriteAllText(Path.Combine(_root, "home", "settings.json"), """{"sandbox":"off"}""");

        var s = SettingsLoader.Load(Path.Combine(_root, "ws"));

        Assert.False(s.ManagedPolicy);
        Assert.Equal("off", s.Sandbox);
    }

    [Fact]
    public void ApplyJson_disableIgnoreConfirmOutsideTheManagedFile_isIgnored()
    {
        // 사용자·프로젝트 레이어가 이 키를 쓰거나 꺼도 반영되지 않는다 — 중앙 파일만 정한다.
        Assert.False(SettingsLoader.ApplyJson(Settings.Default, """{"disableIgnoreConfirm":true}""").DisableIgnoreConfirm);

        var managed = SettingsLoader.ApplyManagedJson(Settings.Default, """{"disableIgnoreConfirm":true}""");
        Assert.True(SettingsLoader.ApplyJson(managed, """{"disableIgnoreConfirm":false}""").DisableIgnoreConfirm);
    }

    [Fact]
    public void DisabledTools_mergeAcrossLayers_andMatchByNameOrPrefix()
    {
        var user = SettingsLoader.ApplyJson(Settings.Default, """{"disabledTools":["ImageCreate"]}""");
        var project = SettingsLoader.ApplyJson(user, """{"disabledTools":["mcp__db__*"]}""",
            allowSensitive: false, allowAutomation: false, allowPermissionRules: false);

        Assert.Equal(["ImageCreate", "mcp__db__*"], project.DisabledTools);
        Assert.True(MoaiCode.Cli.AppBootstrap.IsDisabledTool("ImageCreate", project.DisabledTools));
        Assert.True(MoaiCode.Cli.AppBootstrap.IsDisabledTool("mcp__db__query", project.DisabledTools));
        Assert.False(MoaiCode.Cli.AppBootstrap.IsDisabledTool("ImageCreateX", project.DisabledTools));
        Assert.False(MoaiCode.Cli.AppBootstrap.IsDisabledTool("Bash", project.DisabledTools));
    }
}
