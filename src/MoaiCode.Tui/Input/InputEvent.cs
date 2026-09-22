namespace MoaiCode.Tui.Input;

/// <summary>
/// VT 파서가 원시 바이트를 해석해 내보내는 입력 이벤트. 콘솔 비의존 값 타입 —
/// 기존 소비처가 쓰는 <see cref="ConsoleKeyInfo"/> 를 Key 이벤트에 실어 파급을 최소화한다.
/// (record 라 유닛테스트에서 값 비교로 단언하기 쉽다.)
/// </summary>
public abstract record InputEvent;

/// <summary>일반 키 입력. Key 는 기존 BottomDock/피커의 switch(key.Key)·KeyChar·Modifiers 를 그대로 만족한다.</summary>
public sealed record KeyEvent(ConsoleKeyInfo Key) : InputEvent;

/// <summary>bracketed paste 본문(ESC[200~ … ESC[201~ 사이). 여러 줄 개행 포함 원문.</summary>
public sealed record PasteEvent(string Text) : InputEvent;

/// <summary>포커스 변경(ESC[I 획득 / ESC[O 상실). 현재 TUI 는 소비하지 않지만 파서가 흘려보낼 수 있게 표현.</summary>
public sealed record FocusEvent(bool Gained) : InputEvent;

/// <summary>Ctrl+C(0x03). raw 모드에선 OS 시그널이 안 오므로 파서가 이벤트로 올려 취소 로직에 연결한다.</summary>
public sealed record CancelEvent : InputEvent;

/// <summary>
/// 커서 위치 보고(ESC[&lt;row&gt;;&lt;col&gt;R) — DSR(ESC[6n) 질의에 대한 터미널 응답. 키가 아니라
/// 질의-응답 프로토콜이라 별도 이벤트로 내보낸다. TUI 는 보통 무시하지만(키 루프에서 스킵),
/// 리사이즈 처리가 실제 커서 위치로 터미널 성장 모델을 판별할 때 소비한다.
/// </summary>
public sealed record CursorReportEvent(int Row, int Col) : InputEvent;

public enum MouseAction { Press, Release, Move, WheelUp, WheelDown }

/// <summary>
/// SGR 1006 마우스 이벤트. X/Y 는 1-기준 열/행(터미널 규약). 현재 TUI 는 마우스를 소비하지 않지만
/// 파서·이벤트 기반을 미리 마련한다(후속 소비 대비).
/// </summary>
public sealed record MouseEvent(
    MouseAction Action,
    int Button,
    int X,
    int Y,
    bool Shift,
    bool Alt,
    bool Control) : InputEvent;
