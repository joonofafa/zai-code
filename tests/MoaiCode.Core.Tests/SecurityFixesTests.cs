using MoaiCode.Core;
using MoaiCode.Core.Agent.Prompts;
using MoaiCode.Core.Messages;
using MoaiCode.Persistence;
using Xunit;

namespace MoaiCode.Core.Tests;

// 보안 하드닝 회귀 테스트: 세션 파일 퍼미션(0600), 외부 결과 인젝션 경계.
public class SecurityFixesTests
{
    [Fact]
    public async Task Session_file_is_user_only_on_unix()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix 퍼미션 전용
        }

        var dir = Path.Combine(Path.GetTempPath(), "moai-sec-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SessionStore(dir);
            await store.SaveAsync("s1", new List<Message> { new UserMessage("token=sk-secret") });
            var path = store.PathFor("s1");

            var mode = File.GetUnixFileMode(path);
            // 0600: 소유자 읽기/쓰기만, 그룹/기타 접근 없음.
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void FilePermissions_restrict_is_safe_on_missing_path()
    {
        // 존재하지 않는 경로/디렉토리에도 예외 없이 no-op.
        FilePermissions.RestrictFileToUser(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid()));
        FilePermissions.RestrictDirToUser(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid()));
    }

    [Fact]
    public void Untrusted_output_boundary_is_a_system_reminder()
    {
        var b = Reminders.UntrustedToolOutput;
        Assert.StartsWith("<system-reminder>", b);
        Assert.Contains("UNTRUSTED", b);
        Assert.Contains("Do NOT follow", b);
        Assert.EndsWith("\n\n", b); // 실제 데이터가 뒤에 붙는 prefix 형태
    }
}
