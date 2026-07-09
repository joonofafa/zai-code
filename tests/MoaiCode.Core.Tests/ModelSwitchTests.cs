using MoaiCode.Core.Agent;
using MoaiCode.Providers;
using MoaiCode.Providers.OpenAi;
using Xunit;

namespace MoaiCode.Core.Tests;

// /model 라이브 전환 배선 검증: 모델 id 를 바꾸면 다음 요청부터 반영되고,
// RetryingChatModel 데코레이터가 내부 모델로 위임한다.
public class ModelSwitchTests
{
    private static OpenAiChatModel NewModel(string model = "gpt-4o-mini")
        => new(new HttpClient(), "https://example.test/v1", "sk-x", model);

    [Fact]
    public void OpenAi_current_model_is_settable()
    {
        var m = NewModel();
        Assert.Equal("gpt-4o-mini", m.CurrentModel);
        m.CurrentModel = "gpt-5";
        Assert.Equal("gpt-5", m.CurrentModel);
    }

    [Fact]
    public void OpenAi_ignores_blank_model()
    {
        var m = NewModel("keep-me");
        m.CurrentModel = "";
        m.CurrentModel = "   ";
        Assert.Equal("keep-me", m.CurrentModel);
    }

    [Fact]
    public void Retrying_forwards_model_switch_to_inner()
    {
        var inner = NewModel("a");
        var retry = new RetryingChatModel(inner);
        Assert.IsAssignableFrom<IModelControl>(retry);

        Assert.Equal("a", retry.CurrentModel);
        retry.CurrentModel = "b";
        Assert.Equal("b", retry.CurrentModel);   // 데코레이터 → 내부 모델
        Assert.Equal("b", inner.CurrentModel);   // 같은 인스턴스가 바뀜(세션/서브에이전트 공유)
    }

    [Fact]
    public async Task Retrying_over_non_switchable_is_safe()
    {
        // Echo 는 IModelControl 미구현 → 위임할 대상 없음. no-op / 빈 목록이어야 한다(예외 없음).
        var retry = new RetryingChatModel(new EchoChatModel());
        Assert.Equal("", retry.CurrentModel);
        retry.CurrentModel = "whatever"; // no-op
        Assert.Equal("", retry.CurrentModel);
        Assert.Empty(await retry.ListModelsAsync(CancellationToken.None));
    }
}
