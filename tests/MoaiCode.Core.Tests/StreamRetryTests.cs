using System.Runtime.CompilerServices;
using MoaiCode.Core.Agent;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;
using Xunit;

namespace MoaiCode.Core.Tests;

public class StreamRetryTests
{
    private sealed class TransientEx : Exception, IModelException
    {
        public bool IsContextOverflow => false;
        public bool IsTransient => true;
    }

    // 1차 호출: 일부 텍스트를 흘린 뒤 중간에 끊김(transient). 2차(재시도): 완전한 응답.
    private sealed class FlakyModel : IChatModel
    {
        public int Calls { get; private set; }

        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            IReadOnlyList<Message> messages,
            IReadOnlyList<ITool> tools,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            if (Calls++ == 0)
            {
                yield return new TextDelta("partial-");
                yield return new TextDelta("half");
                throw new TransientEx(); // 응답 도중 스트림 끊김
            }

            yield return new TextDelta("FULL ANSWER");
            yield return new TurnCompleted(new Usage(3, 3), "stop");
        }
    }

    [Fact]
    public async Task MidStreamDrop_DiscardsPartial_RetriesAndKeepsFullAnswer()
    {
        var model = new FlakyModel();
        var engine = new QueryEngine(model, Array.Empty<ITool>());

        var sawNotice = false;
        await foreach (var ev in engine.SubmitAsync("hi"))
        {
            if (ev is StreamNotice)
            {
                sawNotice = true;
            }
        }

        // 재시도가 실제로 일어났고(2회 호출), 사용자에게 안내가 나갔다.
        Assert.Equal(2, model.Calls);
        Assert.True(sawNotice, "a StreamNotice should be emitted on mid-stream retry");

        // 저장된 어시스턴트 메시지는 '부분 응답'이 아니라 '재시도된 완전한 응답'이어야 한다.
        var assistant = engine.Messages.OfType<AssistantMessage>().Last();
        var text = string.Concat(assistant.Content.OfType<TextBlock>().Select(b => b.Text));
        Assert.Equal("FULL ANSWER", text);
        Assert.DoesNotContain("partial", text);
    }
}
