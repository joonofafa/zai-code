using MoaiCode.Cli;
using MoaiCode.Config;
using MoaiCode.Core.Memory;
using Xunit;

namespace MoaiCode.Core.Tests;

/// <summary>
/// 데이터 루트(~/.zaicode) 격리 회귀. 테스트가 HOME 을 임시 경로로 바꿔도 Windows 의
/// SpecialFolder.UserProfile 은 HOME 을 보지 않아, 격리된 줄 알았던 테스트가 사용자의 진짜 settings.json 에 썼다.
/// 이제 MOAI_HOME 이 데이터 루트를 정한다 — 한 군데라도 빠지면 그 파일만 진짜 홈에 남아 샌다.
/// </summary>
[Collection("EnvMutating")]
public sealed class AppPathsTests
{
    [Fact]
    public void Without_MOAI_HOME_the_root_is_dot_zaicode_under_the_user_home()
    {
        var prev = Environment.GetEnvironmentVariable("MOAI_HOME");
        Environment.SetEnvironmentVariable("MOAI_HOME", null);
        try
        {
            Assert.Equal(Path.Combine(AppPaths.UserHome, ".zaicode"), AppPaths.Home);
            Assert.Equal(Path.Combine(AppPaths.UserHome, ".zaicode", "logs", "x.log"), AppPaths.Combine("logs", "x.log"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MOAI_HOME", prev);
        }
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
