using MoaiCode.Core.Agent;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;
using Xunit;

namespace MoaiCode.Core.Tests;

// /resume 회귀: 저장된 세션의 첫 줄에는 '그때의' 시스템 프롬프트가 들어 있다. 그걸 그대로 복원하면
// 이후 개선된 지침·현재 환경이 반영되지 않아, 복원 세션만 옛 프롬프트로 계속 도는 버그가 있었다.
public class SessionRestoreSeedTests
{
    private sealed class StubModel : IChatModel
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            IReadOnlyList<Message> messages, IReadOnlyList<ITool> tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new TurnCompleted(new Usage(1, 1), "stop");
        }
    }

    [Fact]
    public void Restore_uses_current_seed_not_the_saved_system_prompt()
    {
        var engine = new QueryEngine(new StubModel(), Array.Empty<ITool>());
        engine.Seed(new[] { new SystemMessage("CURRENT system prompt (with new guidelines)") });

        // 저장된 세션: 옛 시스템 프롬프트 + 대화.
        engine.Restore(new List<Message>
        {
            new SystemMessage("OLD system prompt (stale)"),
            new UserMessage("hello"),
        });

        var systems = engine.Messages.OfType<SystemMessage>().ToList();
        var single = Assert.Single(systems);
        Assert.Contains("CURRENT", single.Text);          // 현재 seed 로 대체됨
        Assert.DoesNotContain("OLD", single.Text);        // 옛 프롬프트는 버려짐
        Assert.Equal(single, engine.Messages[0]);         // 시스템 프롬프트가 맨 앞

        // 대화 내용은 보존.
        Assert.Contains(engine.Messages, m => m is UserMessage u && u.Text == "hello");
    }
}
