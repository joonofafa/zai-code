namespace MoaiCode.Tui.Mvu;

/// <summary>
/// UI 상태 (Elm식 MVU). 대화 히스토리는 QueryEngine이 소유하므로
/// Model은 세션 UI 플래그만 보유. 설계 근거: ../../CSHARP_PORT_PLAN.md 4.4.
/// </summary>
public sealed record Model(bool Quit = false)
{
    public static Model Initial { get; } = new();
}

/// <summary>상태 전이 메시지.</summary>
public abstract record Msg;

public sealed record QuitRequested : Msg;

public sealed record SessionCleared : Msg;
