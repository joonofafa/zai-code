using MoaiCode.Tui;
using Xunit;

namespace MoaiCode.Core.Tests;

public class AtMentionCompletionTests
{
    [Fact]
    public void GhostSuffix_SlashCommand_StillWorks()
    {
        // '@' 확장 후에도 기존 '/' 명령 ghost 가 그대로 동작해야 한다.
        Assert.Equal("el", LineEditor.GhostSuffix("/mod", new[] { "model", "exit" }));
    }

    [Fact]
    public void GhostSuffix_CompletesFileMention_FromDirectory()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "moai-at-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        File.WriteAllText(Path.Combine(tmp, "foobar.txt"), "x");
        Directory.CreateDirectory(Path.Combine(tmp, "foodir"));
        var empty = Array.Empty<string>();
        try
        {
            // '@<tmp>/foo' → 첫 매치 'foobar.txt'(foobar < foodir), 이미 친 'foo' 제외 나머지가 ghost.
            Assert.Equal("bar.txt", LineEditor.GhostSuffix("@" + Path.Combine(tmp, "foo"), empty));

            // '@<tmp>/food' → 유일 매치 'foodir'(디렉토리) → 나머지 + '/'.
            Assert.Equal("ir/", LineEditor.GhostSuffix("@" + Path.Combine(tmp, "food"), empty));

            // 문장 중간의 '@' 멘션도 끝 토큰 기준으로 동작.
            Assert.Equal("bar.txt", LineEditor.GhostSuffix("review @" + Path.Combine(tmp, "foo"), empty));

            // 매치 없으면 빈 문자열.
            Assert.Equal("", LineEditor.GhostSuffix("@" + Path.Combine(tmp, "zzz"), empty));

            // '@' 로 시작하지 않는 토큰(이메일 등)은 파일 완성하지 않음.
            Assert.Equal("", LineEditor.GhostSuffix("ping user@host", empty));
        }
        finally
        {
            try
            {
                Directory.Delete(tmp, true);
            }
            catch
            {
                // best-effort
            }
        }
    }
}
