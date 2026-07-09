using System.Text;
using System.Text.Json;
using MoaiCode.Core.Agent;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;
using Xunit;

namespace MoaiCode.Core.Tests;

// tool_use ↔ tool_result 짝 보증: 고아 tool_use 가 있으면 모델 호출 전에 합성 결과로 짝을 맞춰
// Anthropic 등 엄격 API 의 400("tool_use ids ... without tool_result")을 자가치유하는지 검증.
public class ToolPairingTests
{
    // 모델 호출 시점의 메시지를 캡처해 tool_use/tool_result 짝을 검사하는 스텁.
    private sealed class CapturingModel : IChatModel
    {
        public IReadOnlyList<Message>? Captured;

        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            IReadOnlyList<Message> messages, IReadOnlyList<ITool> tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Captured = messages.ToList();
            await Task.Yield();
            yield return new TextDelta("ok");
            yield return new TurnCompleted(new Usage(1, 1), "stop");
        }
    }

    private static ToolUseBlock Tu(string id, string name)
        => new(id, name, JsonSerializer.SerializeToElement(new { }));

    [Fact]
    public async Task Orphan_tool_use_gets_synthetic_result_before_send()
    {
        var model = new CapturingModel();
        var engine = new QueryEngine(model, Array.Empty<ITool>());

        // 손상된 히스토리 복원: assistant 가 tool_use 2개(A,B) 냈는데 A 의 result 만 있고 B 는 고아.
        engine.Restore(new List<Message>
        {
            new SystemMessage("sys"),
            new UserMessage("hi"),
            new AssistantMessage(new List<ContentBlock> { Tu("A", "Read"), Tu("B", "Bash") }),
            new ToolResultMessage("A", "fileA", false),
            // B 의 tool_result 없음(고아)
            new UserMessage("계속"),
        });

        await foreach (var _ in engine.SubmitAsync("go", CancellationToken.None)) { }

        // 모델에 전달된 메시지: 각 tool_use 는 그 뒤 연속 tool_result 로 커버돼야 한다.
        var msgs = model.Captured!;
        var ai = msgs.OfType<AssistantMessage>().First(a => a.Content.OfType<ToolUseBlock>().Any());
        var idx = msgs.ToList().IndexOf(ai);
        var provided = new HashSet<string>();
        for (var j = idx + 1; j < msgs.Count && msgs[j] is ToolResultMessage tr; j++)
        {
            provided.Add(tr.ToolUseId);
        }

        Assert.Contains("A", provided);
        Assert.Contains("B", provided); // 고아였던 B 도 합성 결과로 커버됨
    }

    [Fact]
    public async Task Orphan_tool_result_without_tool_use_is_dropped()
    {
        var model = new CapturingModel();
        var engine = new QueryEngine(model, Array.Empty<ITool>());

        engine.Restore(new List<Message>
        {
            new SystemMessage("sys"),
            new UserMessage("hi"),
            new ToolResultMessage("GHOST", "대응 tool_use 없음", false), // 고아 tool_result
            new UserMessage("계속"),
        });

        await foreach (var _ in engine.SubmitAsync("go", CancellationToken.None)) { }

        // 고아 tool_result 는 전송 메시지에서 제거돼야 한다.
        Assert.DoesNotContain(model.Captured!, m => m is ToolResultMessage tr && tr.ToolUseId == "GHOST");
    }

    [Fact]
    public async Task User_message_between_tool_results_is_moved_after_and_dupes_dropped()
    {
        var model = new CapturingModel();
        var engine = new QueryEngine(model, Array.Empty<ITool>());

        // 실제로 터졌던 형태: assistant(A,B) → result(A) → user(리마인더) → result(B)
        engine.Restore(new List<Message>
        {
            new SystemMessage("sys"),
            new UserMessage("hi"),
            new AssistantMessage(new List<ContentBlock> { Tu("A", "Read"), Tu("B", "Grep") }),
            new ToolResultMessage("A", "resA", false),
            new UserMessage("<system-reminder>중복 호출</system-reminder>"),
            new ToolResultMessage("B", "resB", false),
        });

        await foreach (var _ in engine.SubmitAsync("go", CancellationToken.None)) { }

        var msgs = model.Captured!.ToList();
        var ai = msgs.FindIndex(m => m is AssistantMessage a && a.Content.OfType<ToolUseBlock>().Any());

        // assistant 바로 뒤 2개가 A, B 의 tool_result 여야 하고(연속), 그 뒤에 user 가 온다.
        Assert.IsType<ToolResultMessage>(msgs[ai + 1]);
        Assert.IsType<ToolResultMessage>(msgs[ai + 2]);
        Assert.Equal("A", ((ToolResultMessage)msgs[ai + 1]).ToolUseId);
        Assert.Equal("B", ((ToolResultMessage)msgs[ai + 2]).ToolUseId);
        Assert.IsType<UserMessage>(msgs[ai + 3]);

        // 진짜 결과가 보존되고(합성으로 대체되지 않음), 중복은 없다.
        Assert.Equal("resB", ((ToolResultMessage)msgs[ai + 2]).Output);
        Assert.Equal(1, msgs.Count(m => m is ToolResultMessage tr && tr.ToolUseId == "B"));
    }
}
