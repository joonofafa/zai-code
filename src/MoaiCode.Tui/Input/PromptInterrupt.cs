namespace MoaiCode.Tui.Input;

/// <summary>입력 대기 폴링 루프가 외부 사유(백그라운드 셸 완료 등)로 깨어났음을 나타내는 이벤트(실제 키가 아님).</summary>
public sealed record WakeEvent : InputEvent;

/// <summary>
/// 프롬프트 입력 대기를 키 입력 밖의 조건으로 깨우는 통로. 백그라운드 셸 완료 통보처럼 외부에서
/// 발생한 사유가 <see cref="ShouldWake"/> 로 등록되면 LineEditor/BottomDock 의 대기 루프가 이를
/// 확인해 <see cref="WakeSignal"/> 센티넬로 반환한다(ReplApp 이 자동 재개 턴으로 이어감).
/// </summary>
public static class PromptInterrupt
{
    /// <summary>REPL 측 센티넬 — 입력으로 반환되면 깨운 사유를 처리하고 프롬프트로 돌아간다.</summary>
    public const string WakeSignal = "__bg_wake__";

    /// <summary>참이면 입력 대기를 즉시 깨운다. 입력 폴링 주기(40ms)마다 호출되므로 가볍게 유지할 것.</summary>
    public static Func<bool>? ShouldWake;

    /// <summary>깨움 당시 미제출 초안(LineEditor 경로 — 다음 프롬프트의 초기 버퍼로 복원용).</summary>
    public static string? PendingDraft;
}
