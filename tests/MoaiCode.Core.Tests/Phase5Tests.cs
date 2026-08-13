using MoaiCode.Config;
using MoaiCode.Core.Tools;
using MoaiCode.Persistence;
using Xunit;

namespace MoaiCode.Core.Tests;

public class SettingsLoaderTests
{
    [Fact]
    public void ApplyJson_merges_known_keys()
    {
        var s = SettingsLoader.ApplyJson(Settings.Default,
            """
            {
              // 주석 허용
              "model": "gpt-4o-mini",
              "permission": "auto",
              "max_turns": 7,
            }
            """);

        Assert.Equal("gpt-4o-mini", s.Model);
        Assert.Equal(PermissionMode.Auto, s.Permission);
        Assert.Equal(7, s.MaxTurns);
    }

    [Theory]
    [InlineData("en", "en")]
    [InlineData("en-US", "en")]
    [InlineData("ko_KR", "ko")]
    [InlineData("ja", "en")]
    public void ApplyJson_reads_and_normalizes_language(string input, string expected)
    {
        var s = SettingsLoader.ApplyJson(Settings.Default, $$"""{ "language": "{{input}}" }""");
        Assert.Equal(expected, s.Language);
    }

    [Fact]
    public void Env_language_overrides_json_language()
    {
        var previous = Environment.GetEnvironmentVariable("MOAI_LANGUAGE");
        try
        {
            Environment.SetEnvironmentVariable("MOAI_LANGUAGE", "en-GB");
            var s = SettingsLoader.ApplyEnv(Settings.Default with { Language = "ko" });
            Assert.Equal("en", s.Language);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MOAI_LANGUAGE", previous);
        }
    }

    [Fact]
    public void ApplyJson_merges_harness_keys()
    {
        var s = SettingsLoader.ApplyJson(Settings.Default,
            """
            {
              "lint_cmd": "dotnet format --verify-no-changes",
              "testCommand": "dotnet test",
              "auto_lint": true,
              "autoTest": true,
              "repo_map_tokens": 500
            }
            """);

        Assert.Equal("dotnet format --verify-no-changes", s.LintCommand);
        Assert.Equal("dotnet test", s.TestCommand);
        Assert.True(s.AutoLint);
        Assert.True(s.AutoTest);
        Assert.Equal(500, s.RepoMapTokens);
    }

    [Fact]
    public void Checkpoints_defaults_on_and_can_be_disabled()
    {
        Assert.True(Settings.Default.Checkpoints);

        var off = SettingsLoader.ApplyJson(Settings.Default, """{ "checkpoints": false }""");
        Assert.False(off.Checkpoints);

        var onSnake = SettingsLoader.ApplyJson(off with { Checkpoints = false }, """{ "auto_checkpoint": true }""");
        Assert.True(onSnake.Checkpoints);
    }

    [Fact]
    public void ApplyJson_keeps_baseline_for_absent_keys()
    {
        var baseline = Settings.Default with { Model = "keep", MaxTurns = 3 };
        var s = SettingsLoader.ApplyJson(baseline, """{ "permission": "deny" }""");

        Assert.Equal("keep", s.Model);
        Assert.Equal(3, s.MaxTurns);
        Assert.Equal(PermissionMode.Deny, s.Permission);
    }

    [Fact]
    public void ApplyJson_ignores_invalid_json()
    {
        var baseline = Settings.Default with { Model = "x" };
        Assert.Equal("x", SettingsLoader.ApplyJson(baseline, "{ not json").Model);
    }

    [Fact]
    public void ApplyJson_reads_reasoning_effort_aliases_and_normalizes()
    {
        var a = SettingsLoader.ApplyJson(Settings.Default, """{ "reasoning_effort": " HIGH " }""");
        var b = SettingsLoader.ApplyJson(Settings.Default, """{ "effort": "medium" }""");
        var c = SettingsLoader.ApplyJson(Settings.Default, """{ "reasoningEffort": "fast" }""");

        Assert.Equal("high", a.ReasoningEffort);
        Assert.Equal("medium", b.ReasoningEffort);
        Assert.Null(c.ReasoningEffort);
    }

    [Fact]
    public void Env_reasoning_effort_overrides_and_invalid_value_is_ignored()
    {
        var prevA = Environment.GetEnvironmentVariable("MOAI_REASONING_EFFORT");
        var prevB = Environment.GetEnvironmentVariable("OPENAI_REASONING_EFFORT");
        var prevC = Environment.GetEnvironmentVariable("MOAI_EFFORT");
        try
        {
            Environment.SetEnvironmentVariable("MOAI_REASONING_EFFORT", "LOW");
            Environment.SetEnvironmentVariable("OPENAI_REASONING_EFFORT", null);
            Environment.SetEnvironmentVariable("MOAI_EFFORT", null);
            Assert.Equal("low", SettingsLoader.ApplyEnv(Settings.Default).ReasoningEffort);

            Environment.SetEnvironmentVariable("MOAI_REASONING_EFFORT", "invalid");
            Assert.Null(SettingsLoader.ApplyEnv(Settings.Default).ReasoningEffort);

            Environment.SetEnvironmentVariable("MOAI_REASONING_EFFORT", null);
            Environment.SetEnvironmentVariable("OPENAI_REASONING_EFFORT", "medium");
            Assert.Equal("medium", SettingsLoader.ApplyEnv(Settings.Default).ReasoningEffort);

            Environment.SetEnvironmentVariable("OPENAI_REASONING_EFFORT", null);
            Environment.SetEnvironmentVariable("MOAI_EFFORT", "high");
            Assert.Equal("high", SettingsLoader.ApplyEnv(Settings.Default).ReasoningEffort);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MOAI_REASONING_EFFORT", prevA);
            Environment.SetEnvironmentVariable("OPENAI_REASONING_EFFORT", prevB);
            Environment.SetEnvironmentVariable("MOAI_EFFORT", prevC);
        }
    }

    [Fact]
    public void Default_max_turns_is_40()
    {
        // 다단계·멀티페이즈 작업이 완주하도록 상향(12→25→40). 페이즈 경계에선 별도로 턴 예산이 리셋된다.
        Assert.Equal(40, Settings.Default.MaxTurns);
    }

    [Fact]
    public void Env_MOAI_MAX_TURNS_overrides()
    {
        var prev = Environment.GetEnvironmentVariable("MOAI_MAX_TURNS");
        try
        {
            Environment.SetEnvironmentVariable("MOAI_MAX_TURNS", "80");
            Assert.Equal(80, SettingsLoader.ApplyEnv(Settings.Default).MaxTurns);

            Environment.SetEnvironmentVariable("MOAI_MAX_TURNS", "0"); // 무효 → 기본 유지
            Assert.Equal(40, SettingsLoader.ApplyEnv(Settings.Default).MaxTurns);

            Environment.SetEnvironmentVariable("MOAI_MAX_TURNS", "abc"); // 파싱 실패 → 기본 유지
            Assert.Equal(40, SettingsLoader.ApplyEnv(Settings.Default).MaxTurns);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MOAI_MAX_TURNS", prev);
        }
    }
}

public class CredentialStoreTests : IDisposable
{
    private readonly string _path;

    public CredentialStoreTests()
        => _path = Path.Combine(Path.GetTempPath(), "occs-cred-" + Guid.NewGuid().ToString("n") + ".json");

    public void Dispose()
    {
        try
        {
            File.Delete(_path);
        }
        catch
        {
            // best-effort
        }
    }

    [Fact]
    public void Set_then_get_roundtrips()
    {
        var store = new FileCredentialStore(_path);
        Assert.Null(store.Get("OPENAI_API_KEY"));

        store.Set("OPENAI_API_KEY", "sk-test");
        Assert.Equal("sk-test", new FileCredentialStore(_path).Get("OPENAI_API_KEY"));
        Assert.Contains("OPENAI_API_KEY", new FileCredentialStore(_path).Keys());
    }
}

public class HistoryStoreTests : IDisposable
{
    private readonly string _path;

    public HistoryStoreTests()
        => _path = Path.Combine(Path.GetTempPath(), "occs-hist-" + Guid.NewGuid().ToString("n") + ".jsonl");

    public void Dispose()
    {
        try
        {
            File.Delete(_path);
        }
        catch
        {
            // best-effort
        }
    }

    [Fact]
    public async Task Append_and_recent_preserve_order_and_limit()
    {
        var store = new HistoryStore(_path);
        for (var i = 1; i <= 5; i++)
        {
            await store.AppendAsync($"cmd{i}");
        }

        var recent = await store.RecentAsync(3);
        Assert.Equal(new[] { "cmd3", "cmd4", "cmd5" }, recent);
    }

    [Fact]
    public async Task Recent_on_missing_file_is_empty()
    {
        var store = new HistoryStore(_path);
        Assert.Empty(await store.RecentAsync(10));
    }
}

public class CheckpointStoreTests : IDisposable
{
    private readonly string _dir;

    public CheckpointStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "occs-checkpoint-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }

    [Fact]
    public async Task Create_list_and_restore_roundtrip()
    {
        var file = Path.Combine(_dir, "a.txt");
        await File.WriteAllTextAsync(file, "one");
        var store = new CheckpointStore(_dir, Path.Combine(_dir, ".checkpoints"));

        var first = await store.CreateAsync("first");
        await File.WriteAllTextAsync(file, "two");
        var second = await store.CreateAsync("second");

        Assert.NotEqual(first, second);
        Assert.True((await store.ListAsync()).Count >= 2);

        await File.WriteAllTextAsync(file, "three");
        await store.RestoreAsync(first);

        Assert.Equal("one", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task Restore_removes_files_created_after_checkpoint()
    {
        var keep = Path.Combine(_dir, "keep.txt");
        await File.WriteAllTextAsync(keep, "v1");
        var store = new CheckpointStore(_dir, Path.Combine(_dir, ".checkpoints"));

        var baseCp = await store.CreateAsync("base");

        // 체크포인트 이후: 새 파일 추가 + 기존 파일 수정
        var added = Path.Combine(_dir, "added.txt");
        await File.WriteAllTextAsync(added, "new");
        await File.WriteAllTextAsync(keep, "v2");

        await store.RestoreAsync(baseCp);

        Assert.False(File.Exists(added));                       // 이후 생긴 파일은 제거됨
        Assert.Equal("v1", await File.ReadAllTextAsync(keep));  // 수정은 롤백됨
    }

    // 홈/루트 워크스페이스는 체크포인트 거부 (디스크 폭주 117G 사고 재발 방지).
    [Fact]
    public void Home_and_root_are_not_checkpointable()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(CheckpointStore.IsCheckpointable(home));
        Assert.False(CheckpointStore.IsCheckpointable(Path.GetPathRoot(home)!));
        // 홈의 상위(예: /home)도 거부
        var parent = Path.GetDirectoryName(home.TrimEnd(Path.DirectorySeparatorChar));
        if (!string.IsNullOrEmpty(parent))
        {
            Assert.False(CheckpointStore.IsCheckpointable(parent));
        }
    }

    [Fact]
    public void Real_project_dir_is_checkpointable()
    {
        Assert.True(CheckpointStore.IsCheckpointable(_dir)); // temp 하위 = 정상
    }

    // 홈에서는 CreateAsync 가 git 저장소를 만들지 않고 no-op (빈 문자열) 이어야 한다.
    [Fact]
    public async Task Create_is_noop_in_home()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var store = new CheckpointStore(home, Path.Combine(_dir, ".cp-home"));
        var id = await store.CreateAsync("should-not-run");
        Assert.Equal(string.Empty, id);
        Assert.False(Directory.Exists(Path.Combine(_dir, ".cp-home"))); // 저장소 미생성
    }
}
