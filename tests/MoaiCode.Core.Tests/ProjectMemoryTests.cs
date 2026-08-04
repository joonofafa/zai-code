using MoaiCode.Core.Memory;
using Xunit;

namespace MoaiCode.Core.Tests;

public class ProjectMemoryTests
{
    [Fact]
    public void Save_Load_List_Delete_RoundTrip()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "moai-mem-test-" + Guid.NewGuid().ToString("N"));
        var dir = ProjectMemory.Dir(cwd);
        try
        {
            // 처음엔 인덱스 없음.
            Assert.Null(ProjectMemory.LoadIndex(cwd));

            // 저장: name은 kebab-case 슬러그로 정규화.
            var msg = ProjectMemory.Save(cwd, "Deploy Steps", "how to deploy", "project", "run publish-all.sh then rsync");
            Assert.Contains("deploy-steps", msg);
            Assert.True(File.Exists(Path.Combine(dir, "deploy-steps.md")));

            // 인덱스에 등재(제목 + 설명 hook).
            var idx = ProjectMemory.LoadIndex(cwd);
            Assert.NotNull(idx);
            Assert.Contains("deploy-steps", idx!);
            Assert.Contains("how to deploy", idx!);

            // 본문/타입 frontmatter 확인.
            var body = File.ReadAllText(Path.Combine(dir, "deploy-steps.md"));
            Assert.Contains("type: project", body);
            Assert.Contains("run publish-all.sh then rsync", body);

            Assert.Contains("deploy-steps", ProjectMemory.List(cwd));

            // 같은 name 저장 = 덮어쓰기(중복 파일 안 생김).
            ProjectMemory.Save(cwd, "deploy-steps", "updated", "project", "v2 body");
            Assert.Contains("v2 body", File.ReadAllText(Path.Combine(dir, "deploy-steps.md")));
            var factCount = Directory.GetFiles(dir, "*.md")
                .Count(f => !string.Equals(Path.GetFileName(f), "MEMORY.md", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(1, factCount);

            // 잘못된 type은 project로 폴백.
            ProjectMemory.Save(cwd, "pref", "a pref", "bogus", "x");
            Assert.Contains("type: project", File.ReadAllText(Path.Combine(dir, "pref.md")));

            // 삭제.
            Assert.Contains("Deleted", ProjectMemory.Delete(cwd, "deploy-steps"));
            Assert.False(File.Exists(Path.Combine(dir, "deploy-steps.md")));
            Assert.DoesNotContain("deploy-steps", ProjectMemory.LoadIndex(cwd) ?? "");
        }
        finally
        {
            try
            {
                var projectDir = Path.GetDirectoryName(dir);
                if (projectDir is not null)
                {
                    Directory.Delete(projectDir, recursive: true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    [Fact]
    public void Keying_IsRepoRoot_SubdirectoriesShareOneStore()
    {
        // 임시 repo(가짜 .git) 루트 + 중첩 하위 디렉토리 구성.
        var repoRoot = Path.Combine(Path.GetTempPath(), "moai-repo-" + Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(repoRoot, "src", "deep");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
        var memDir = ProjectMemory.Dir(repoRoot);
        try
        {
            // repo 루트와 하위 디렉토리가 같은 메모리 디렉터리로 해석돼야 한다.
            Assert.Equal(ProjectMemory.Dir(repoRoot), ProjectMemory.Dir(nested));

            // 하위 디렉토리에서 저장 → repo 루트에서 조회 가능(공유 확인).
            ProjectMemory.Save(nested, "shared-note", "from subdir", "project", "hello");
            Assert.Contains("shared-note", ProjectMemory.LoadIndex(repoRoot) ?? "");
        }
        finally
        {
            try
            {
                var projectDir = Path.GetDirectoryName(memDir);
                if (projectDir is not null)
                {
                    Directory.Delete(projectDir, recursive: true);
                }

                Directory.Delete(repoRoot, recursive: true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
