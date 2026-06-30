using System.Runtime.CompilerServices;
using MoaiCode.Core.Agent;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;

namespace MoaiCode.Providers;

/// <summary>
/// 오프라인 데모용 스텁 모델 — API 키 없이 walking skeleton을 돌리기 위함.
/// Phase 1에서 SSE 스트리밍 기반 실제 OpenAI/Anthropic 구현으로 대체.
/// </summary>
public sealed class EchoChatModel : IChatModel
{
    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        IReadOnlyList<Message> messages,
        IReadOnlyList<ITool> tools,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var lastUser = messages.OfType<UserMessage>().LastOrDefault();
        var reply = $"echo: {lastUser?.Text ?? "(empty)"}";

        var outTokens = 0;
        foreach (var word in reply.Split(' '))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(40, ct);
            yield return new TextDelta(word + " ");
            outTokens++;
        }

        yield return new TurnCompleted(new Usage(InputTokens: 0, OutputTokens: outTokens), "end_turn");
    }
}
