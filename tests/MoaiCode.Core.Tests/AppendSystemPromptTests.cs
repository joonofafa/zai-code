using System.Text.Json;
using MoaiCode.Core.Agent;
using MoaiCode.Core.Messages;
using MoaiCode.Persistence;
using MoaiCode.Providers;
using MoaiCode.Core.Tools;
using Xunit;

namespace MoaiCode.Core.Tests;

/// <summary>
/// --append-system-prompt(claude CLI 호환) 회귀: 문구가 시스템 프롬프트 끝에 덧붙고
/// (1) 세션 저장에서는 시스템 프롬프트가 빠지고 (2) --resume 복원 시 현재 실행의
/// 프롬프트가 그대로 살아있으며 (3) 컴팩션 뒤에도 유지되는지.
/// AppBootstrap.BuildAsync 전체를 띄우기 어려워(MCP/네트워크), 여기선 엔진·저장소
/// 계약이 요구사항을 실제로 지키는지를 AppBootstrap 과 같은 조립 방식으로 검증한다.
/// </summary>
[Collection("EnvMutating")]
public class AppendSystemPromptTests
{
    private static string BuildPrompt(string? append) =>
        "BASE SYSTEM PROMPT" + (string.IsNullOrEmpty(append) ? "" : "\n\n" + append);

    private static CapturingModel NewEngine(string? append, out QueryEngine engine)
    {
        var model = new CapturingModel();
        engine = new QueryEngine(model, Array.Empty<ITool>(), maxTurns: 2);
        engine.Seed(new[] { new SystemMessage(BuildPrompt(append)) });
        return model;
    }

    // SubmitAsync 는 IAsyncEnumerable<StreamEvent> 를 돌려준다 — 끝까지 소비해야 턴이 완결된다.
    private static async Task RunAsync(QueryEngine engine, string prompt)
    {
        await foreach (var _ in engine.SubmitAsync(prompt, CancellationToken.None))
        {
        }
    }

    // 1) 덧붙인 문구가 시스템 프롬프트 맨 끝에, 기존 내용 뒤에 그대로 붙는다.
    [Fact]
    public async Task AppendedPrompt_lands_at_end_of_system_prompt()
    {
        var append = "Always begin your reply with the word PINEAPPLE.\nline two";
        var model = NewEngine(append, out var engine);
        await RunAsync(engine, "hello");
        var system = Assert.IsType<SystemMessage>(Assert.Single(model.Sent.Where(m => m is SystemMessage)));
        Assert.StartsWith("BASE SYSTEM PROMPT", system.Text, StringComparison.Ordinal);
        Assert.EndsWith(append, system.Text, StringComparison.Ordinal);
        Assert.Contains("\n\n" + append, system.Text, StringComparison.Ordinal);
    }

    // 값이 없으면 지금과 완전히 같다(덧붙임 없음).
    [Fact]
    public async Task NoOption_output_is_unchanged()
    {
        var model = NewEngine(null, out var engine);
        await RunAsync(engine, "hello");
        var system = Assert.IsType<SystemMessage>(Assert.Single(model.Sent.Where(m => m is SystemMessage)));
        Assert.Equal("BASE SYSTEM PROMPT", system.Text);
    }

    // 2) 세션 저장에는 시스템 프롬프트(덧붙임 포함)가 기록되지 않는다 — 매 실행 인자로 다시 받는다.
    [Fact]
    public async Task Saved_session_excludes_system_prompt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "moai-asp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SessionStore(dir);
            var model = NewEngine("SECRET-APPENDIX", out var engine);
            await RunAsync(engine, "hello");
            await store.SaveAsync("s1", engine.Messages, CancellationToken.None);

            var raw = await File.ReadAllTextAsync(store.PathFor("s1"), CancellationToken.None);
            Assert.DoesNotContain("SECRET-APPENDIX", raw);
            Assert.DoesNotContain("BASE SYSTEM PROMPT", raw);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // 3) --resume: 저장본(시스템 없음)을 복원한 뒤에도 이번 실행의 시스템 프롬프트가 앞에 있다.
    [Fact]
    public async Task Resume_keeps_current_run_system_prompt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "moai-asp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SessionStore(dir);
            var model = NewEngine("OLD-APPENDIX", out var first);
            await RunAsync(first, "hello");
            await store.SaveAsync("s1", first.Messages, CancellationToken.None);

            // 다음 실행: 다른 덧붙임으로 새 엔진을 만들고 복원.
            var model2 = new CapturingModel();
            var second = new QueryEngine(model2, Array.Empty<ITool>(), maxTurns: 2);
            second.Seed(new[] { new SystemMessage(BuildPrompt("NEW-APPENDIX")) });
            second.Restore(await store.LoadAsync("s1", CancellationToken.None));
            await RunAsync(second, "again");

            var system = Assert.IsType<SystemMessage>(Assert.Single(model2.Sent.Where(m => m is SystemMessage)));
            Assert.EndsWith("NEW-APPENDIX", system.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("OLD-APPENDIX", system.Text);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // 4) 컴팩션 뒤에도 시스템 프롬프트(seed)는 유지된다 — QueryEngine 컴팩션 계약 검증.
    [Fact]
    public async Task Compaction_preserves_system_prompt()
    {
        var append = "PINEAPPLE-RULE";
        var model = NewEngine(append, out var engine);
        engine.SetContextWindow(2000);   // 낮은 창 → 컴팩션 강제
        for (var i = 0; i < 30; i++)
        {
            await RunAsync(engine, $"filler turn {i} " + new string('x', 200));
        }

        // 모델에 갔다 온 마지막 요청에도 시스템 프롬프트가 그대로 있다.
        var last = model.Sent.Last(m => m is SystemMessage);
        Assert.EndsWith(append, ((SystemMessage)last).Text, StringComparison.Ordinal);
    }

    /// <summary>보낸 메시지를 전부 기록하는 가짜 모델(응답은 짧은 텍스트 1개).</summary>
    private sealed class CapturingModel : IChatModel
    {
        public readonly List<Message> Sent = new();

        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            IReadOnlyList<Message> messages,
            IReadOnlyList<ITool> tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Sent.Clear();
            Sent.AddRange(messages);
            await Task.Yield();
            yield return new TextDelta("ok");
            yield return new TurnCompleted(new Usage(1, 1), "end_turn");
        }
    }
}
