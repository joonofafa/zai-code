using MoaiCode.Core.Agent.Prompts;
using Xunit;

namespace MoaiCode.Core.Tests;

public class SystemPromptMemoryTests
{
    [Fact]
    public void Build_IncludesMemorySection_WithIndex()
    {
        var ctx = new PromptContext
        {
            WorkingDirectory = Path.Combine(Path.GetTempPath(), "moai-prompt-test"),
            MemoryIndex = "# Memory Index\n\n- [deploy-steps](deploy-steps.md) — how to deploy",
        };

        var prompt = SystemPromptBuilder.Build(ctx);

        Assert.Contains("# Memory", prompt);
        Assert.Contains("Memory tool", prompt);
        Assert.Contains("deploy-steps", prompt);       // 인덱스 내용 주입 확인
        Assert.Contains("how to deploy", prompt);
    }

    [Fact]
    public void Build_IncludesMemorySection_WhenEmpty()
    {
        var ctx = new PromptContext
        {
            WorkingDirectory = Path.Combine(Path.GetTempPath(), "moai-prompt-test-empty"),
        };

        var prompt = SystemPromptBuilder.Build(ctx);

        Assert.Contains("# Memory", prompt);
        Assert.Contains("No memories saved yet", prompt);
    }
}
