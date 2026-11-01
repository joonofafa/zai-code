using MoaiCode.Core.Agent;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;

namespace MoaiCode.Providers;

/// <summary>
/// 대화 중 프로바이더를 바꿀 수 있게 실제 모델을 감싼다(/provider). 엔진·하위 에이전트·위험 판정 분류기가
/// 같은 인스턴스를 쥐고 있으므로, 안쪽만 바꾸면 다음 요청부터 모두 새 프로바이더로 간다.
/// 이미 시작된 스트림은 시작할 때의 모델로 끝까지 간다.
/// </summary>
public sealed class SwitchableChatModel : IChatModel, IModelControl, IOutputBoost
{
    private IChatModel _inner;

    public SwitchableChatModel(IChatModel inner) => _inner = inner;

    public IChatModel Inner
    {
        get => Volatile.Read(ref _inner);
        set => Volatile.Write(ref _inner, value);
    }

    public IAsyncEnumerable<StreamEvent> StreamAsync(
        IReadOnlyList<Message> messages, IReadOnlyList<ITool> tools, CancellationToken ct)
        => Inner.StreamAsync(messages, tools, ct);

    public void BoostNextRequest() => (Inner as IOutputBoost)?.BoostNextRequest();

    public string CurrentModel
    {
        get => (Inner as IModelControl)?.CurrentModel ?? "";
        set { if (Inner is IModelControl c) c.CurrentModel = value; }
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
        => Inner is IModelControl c
            ? c.ListModelsAsync(ct)
            : Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
}
