using MoaiCode.Cli;
using MoaiCode.Config;
using MoaiCode.Core.Memory;
using Xunit;

namespace MoaiCode.Core.Tests;

/// <summary>
/// 데이터 루트(~/.mate) 격리·이전 회귀. 테스트가 HOME 을 임시 경로로 바꿔도 Windows 의
/// SpecialFolder.UserProfile 은 HOME 을 보지 않아, 격리된 줄 알았던 테스트가 사용자의 진짜 settings.json 에 썼다.
/// 이제 MOAI_HOME 이 데이터 루트를 정한다 — 한 군데라도 빠지면 그 파일만 진짜 홈에 남아 샌다.
/// </summary>
[Collection("EnvMutating")]
public sealed class AppPathsTests
{
    [Fact]
    public void Without_MOAI_HOME_the_root_is_dot_mate_under_the_user_home()
    {
        var home = TempHome();
        try
        {
            Assert.Equal(Path.Combine(home, ".mate"), AppPaths.HomeUnder(home));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void Legacy_root_is_moved_and_a_link_is_left_behind()
    {
        // 1.4.0 리브랜딩: ~/.zaicode 의 설정·키·세션이 새 이름 ~/.mate 로 옮겨져야 한다. 옛 자리 링크는
        // 아직 떠 있는 옛 판과 그 경로를 읽는 텔레그램 브리지를 위한 것이다.
        var home = TempHome();
        try
        {
            var legacy = Path.Combine(home, ".zaicode");
            Directory.CreateDirectory(Path.Combine(legacy, "logs"));
            File.WriteAllText(Path.Combine(legacy, "settings.json"), """{"language":"ko"}""");

            Assert.Equal(legacy, AppPaths.HomeUnder(home));   // 옮기기 전(또는 옮기기 실패)에는 옛 폴더를 계속 쓴다
            Assert.True(AppPaths.MigrateLegacyHome(home));

            var current = Path.Combine(home, ".mate");
            Assert.Equal(current, AppPaths.HomeUnder(home));
            Assert.Equal("""{"language":"ko"}""", File.ReadAllText(Path.Combine(current, "settings.json")));
            Assert.True(Directory.Exists(Path.Combine(current, "logs")));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(".mate", new DirectoryInfo(legacy).LinkTarget);
                Assert.True(File.Exists(Path.Combine(legacy, "settings.json")));
            }

            Assert.False(AppPaths.MigrateLegacyHome(home));   // 두 번째 실행은 아무것도 안 한다(옛 자리가 링크)
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void Existing_new_root_is_never_overwritten()
    {
        var home = TempHome();
        try
        {
            Directory.CreateDirectory(Path.Combine(home, ".zaicode"));
            File.WriteAllText(Path.Combine(home, ".zaicode", "settings.json"), "old");
            Directory.CreateDirectory(Path.Combine(home, ".mate"));
            File.WriteAllText(Path.Combine(home, ".mate", "settings.json"), "new");

            Assert.False(AppPaths.MigrateLegacyHome(home));
            Assert.Equal("new", File.ReadAllText(Path.Combine(home, ".mate", "settings.json")));
            Assert.Equal("old", File.ReadAllText(Path.Combine(home, ".zaicode", "settings.json")));
            Assert.Equal(Path.Combine(home, ".mate"), AppPaths.HomeUnder(home));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void MOAI_HOME_turns_migration_off()
    {
        var prev = Environment.GetEnvironmentVariable("MOAI_HOME");
        Environment.SetEnvironmentVariable("MOAI_HOME", Path.Combine(Path.GetTempPath(), "zai-home-off"));
        try
        {
            Assert.False(AppPaths.MigrateLegacyHome());   // 진짜 홈의 ~/.zaicode 를 건드리지 않는다
        }
        finally
        {
            Environment.SetEnvironmentVariable("MOAI_HOME", prev);
        }
    }

    private static string TempHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "zai-userhome-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(home);
        return home;
    }

    [Fact]
    public void Every_data_path_follows_MOAI_HOME()
    {
        var root = Path.Combine(Path.GetTempPath(), "zai-home-" + Guid.NewGuid().ToString("n"));
        var prev = Environment.GetEnvironmentVariable("MOAI_HOME");
        Environment.SetEnvironmentVariable("MOAI_HOME", root);
        try
        {
            var cwd = Path.Combine(Path.GetTempPath(), "zai-proj");
            var paths = new[]
            {
                SettingsWriter.DefaultPath,
                MoaiLog.FilePath,
                BundledSkills.Dir,
                ProjectMemory.Dir(cwd),
                ProjectMemory.SessionsDir(cwd),
            };
            foreach (var p in paths)
            {
                Assert.StartsWith(root + Path.DirectorySeparatorChar, p);
            }

            // 로더도 같은 루트에서 읽는다.
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "settings.json"), """{"logLevel":"trace"}""");
            Assert.Equal("trace", SettingsLoader.Load(cwd).LogLevel);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MOAI_HOME", prev);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
