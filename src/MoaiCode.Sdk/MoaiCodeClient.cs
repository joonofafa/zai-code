using MoaiCode.Core.Agent;
using MoaiCode.Core.Messages;

namespace MoaiCode.Sdk;

/// <summary>
/// 공개 SDK 진입점 (TS의 src/entrypoints/sdk 대응, 스텁).
/// 외부 통합에서 MoaiCode를 라이브러리로 임베드할 때 사용.
/// </summary>
public sealed class MoaiCodeClient
{
    private readonly QueryEngine _engine;

    public MoaiCodeClient(QueryEngine engine) => _engine = engine;

    public void Seed(IEnumerable<Message> initial) => _engine.Seed(initial);

    public IReadOnlyList<Message> Messages => _engine.Messages;

    public IAsyncEnumerable<StreamEvent> QueryAsync(
        string userInput, CancellationToken ct = default)
        => _engine.SubmitAsync(userInput, ct);
}
