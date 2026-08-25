using MoaiCode.Core.Memory;
using Xunit;

namespace MoaiCode.Core.Tests;

// 세션/메모리는 cwd 단위로 갈라 담긴다. 슬러그가 lossy 하면 서로 다른 프로젝트가 같은 칸을
// 쓰게 되므로(예전엔 /a/moai-code 와 /a/moai/code 가 충돌), 단사성이 핵심 계약이다.
public sealed class ProjectScopeTests
{
    [Fact]
    public void Slug_is_injective_dash_is_only_a_separator()
    {
        var dashInName = ProjectMemory.SessionsDir("/tmp/proj/moai-code");
        var nestedDir = ProjectMemory.SessionsDir("/tmp/proj/moai/code");

        // 경로 안의 '-' 는 %2D 로 이스케이프되고, 구분자 '-' 만 그대로 남는다.
        Assert.Contains("moai%2Dcode", dashInName);
        Assert.Contains("moai-code", nestedDir);
        Assert.NotEqual(dashInName, nestedDir);
    }

    [Fact]
    public void Sessions_and_memory_share_one_project_dir()
    {
        const string Cwd = "/tmp/proj/shared";
        Assert.Equal(
            Directory.GetParent(ProjectMemory.Dir(Cwd))!.FullName,
            Directory.GetParent(ProjectMemory.SessionsDir(Cwd))!.FullName);
    }

    [Fact]
    public void Subdirectory_gets_its_own_scope()
    {
        // git repo 루트로 접히지 않는다 — 하위 디렉토리는 별도 스코프.
        Assert.NotEqual(ProjectMemory.Dir("/tmp/proj/repo"), ProjectMemory.Dir("/tmp/proj/repo/src/deep"));
        Assert.NotEqual(
            ProjectMemory.SessionsDir("/tmp/proj/repo"),
            ProjectMemory.SessionsDir("/tmp/proj/repo/src/deep"));
    }

    [Fact]
    public void Special_characters_survive_without_collision()
    {
        // 공백/한글/틸드가 섞여도 서로 다른 경로는 서로 다른 슬러그가 된다.
        var a = ProjectMemory.Dir("/tmp/proj/a b");
        var b = ProjectMemory.Dir("/tmp/proj/a%20b");
        Assert.NotEqual(a, b);
        Assert.Contains("a%20b", a);
        Assert.Contains("a%2520b", b);   // '%' 자신이 %25 로 이스케이프된다
    }
}
