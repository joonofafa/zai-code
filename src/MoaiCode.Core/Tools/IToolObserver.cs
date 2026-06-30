using MoaiCode.Core.Messages;

namespace MoaiCode.Core.Tools;

/// <summary>
/// QueryEngine tool execution hook. Implementations can snapshot state before a tool
/// runs, or inject verification output after a tool completes.
/// </summary>
public interface IToolObserver
{
    ValueTask BeforeToolAsync(ITool tool, ToolUseBlock call, ToolContext context, CancellationToken ct);

    ValueTask<string?> AfterToolAsync(
        ITool tool,
        ToolUseBlock call,
        ToolContext context,
        string output,
        bool isError,
        CancellationToken ct);

    /// <summary>
    /// 한 어시스턴트 턴의 모든 툴 실행이 끝난 뒤 호출. 무거운 검증(lint/test)을 매 편집마다가 아니라
    /// 턴당 1회로 디바운스하는 지점. 주입할 관찰 메시지가 있으면 반환(없으면 null).
    /// </summary>
    ValueTask<string?> AfterTurnAsync(ToolContext context, CancellationToken ct);
}

public sealed class NullToolObserver : IToolObserver
{
    public static NullToolObserver Instance { get; } = new();

    private NullToolObserver()
    {
    }

    public ValueTask BeforeToolAsync(ITool tool, ToolUseBlock call, ToolContext context, CancellationToken ct)
        => ValueTask.CompletedTask;

    public ValueTask<string?> AfterToolAsync(
        ITool tool,
        ToolUseBlock call,
        ToolContext context,
        string output,
        bool isError,
        CancellationToken ct)
        => ValueTask.FromResult<string?>(null);

    public ValueTask<string?> AfterTurnAsync(ToolContext context, CancellationToken ct)
        => ValueTask.FromResult<string?>(null);
}
