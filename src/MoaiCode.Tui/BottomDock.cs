using System.Text;
using System.Threading;

namespace MoaiCode.Tui;

/// <summary>
/// 하단 고정 상태줄 + 입력창 (opt-in: MOAI_BOTTOM_DOCK=1).
///
/// DECSTBM 스크롤 영역(`\x1b[1;{bottom}r`)으로 화면 위쪽만 스크롤되게 하고, 남은 하단 줄에
/// 상태줄과 입력창을 절대 위치로 그린다. 스트리밍 출력은 위 영역에서 자연스럽게 스크롤되고
/// 입력/상태는 그대로 고정된다(irssi/weechat 방식). 입력 wrap 시 예약 줄 수를 동적으로 조정한다.
///
/// 인라인 LineEditor 는 그대로 두고(리스크 격리), 여기서만 자체 키 루프를 돈다.
/// 입력 wrap 계산·표시폭·프롬프트/배경은 LineEditor 의 internal 헬퍼를 공유한다.
/// </summary>
public sealed class BottomDock
{
    private readonly Func<string> _status;
    private int _reserved;      // 현재 예약된 하단 줄 수(상태 1 + 입력행)
    private bool _installed;    // 스크롤 영역이 설정돼 있는가
    private IReadOnlyList<string> _slash = Array.Empty<string>();  // ghost 자동완성용 명령 목록
    private bool _shell;   // '!' 셸 모드: 입력창 빨강 배경 + '❯'/'!' 미표시. 버퍼엔 '!'를 넣지 않는다.
    private int _lastW, _lastH;   // 마지막으로 그린 터미널 크기(리사이즈 감지용)
    private bool _shrunkRecently; // 직전 리사이즈에서 축소했는가(재성장 시 스크롤백 잔상 되돌아옴 방지)

    // 입력 상태(영구): 프롬프트 편집과 턴 중 편집이 같은 버퍼·히스토리를 공유해 입력창이 항상 동일하게
    // 유지되도록 필드로 둔다(예전엔 ReadLine 지역변수였음).
    private readonly StringBuilder _buf = new();
    private int _pos;
    private IReadOnlyList<string> _hist = Array.Empty<string>();
    private int _histIdx;
    private string _savedCurrent = "";
    private Func<string>? _cycleMode;

    /// <summary>HandleEvent 한 번의 결과 — ReadLine/턴 루프가 반환/큐잉을 결정한다.</summary>
    public enum ComposerOutcome { None, Submit, SubmitShell, Quit, Keymap }

    private const int ResizePollMs = 40;   // 키 대기 중 리사이즈 폴링 주기

    // 브레인스토밍 모드: 입력창 배경을 어두운 파랑으로 강조하고 숨쉬듯 펄스(어두운→덜 어두운→어두운).
    // 40ms 폴에 편승해 프레임마다 음영을 바꿔 입력행만 다시 그린다(별도 스레드 없음).
    private bool _brainstorm;
    private int _animTick;
    // 턴 모드: 모델이 응답하는 동안에도 composer 를 하단에 그대로 유지한다. composer 는 스크롤 영역 아래에
    // 있어 위 영역 스트리밍 출력에 안 씻긴다. 이 모드에선 Draw 가 (1) 스크롤 영역을 재설정하지 않고
    // (2) 실제 커서 대신 저장/복원(7/8)으로 그려 출력 커서(영역 하단)를 보존하며 (3) 가짜 캐럿 블록을 쓴다.
    private bool _turnMode;
    private readonly object _drawLock = new();
    private int _animShade = BrainstormPalette[0];
    private static readonly int[] BrainstormPalette = { 17, 18, 19, 20, 19, 18 }; // 256색 파랑 음영(숨쉬기)
    private const int AnimTicksPerFrame = 3;   // 40ms*3 ≈ 120ms/프레임

    public BottomDock(Func<string> status) => _status = status;

    private static int Height()
    {
        try { var h = Console.WindowHeight; return h < 1 ? 24 : h; }
        catch { return 24; }
    }

    private static int Width()
    {
        try { var w = Console.WindowWidth; return w < 1 ? 80 : w; }
        catch { return 80; }
    }

    /// <summary>하단 고정이 의미 있는 최소 높이(활동/라인/입력/라인/상태 = 최소 5줄 예약 + 스크롤 여유).</summary>
    public static bool Fits() => Height() >= 9;

    /// <summary>스크롤 영역 해제 + 커서를 맨 아래로. REPL 종료/전환 시 반드시 호출.</summary>
    public void Teardown()
    {
        if (!_installed)
        {
            return;
        }

        var h = Height();
        Console.Write($"\x1b[?25h\x1b[r\x1b[{h};1H\r\n"); // 커서 복원 + 영역 해제 + 맨 아래로
        _installed = false;
        _reserved = 0;
    }

    /// <summary>하단 상태줄+입력창을 그린다(현재 입력 버퍼/커서 반영). 입력 wrap 만큼 예약 줄 조정.</summary>
    private void Draw(StringBuilder buf, int pos, string? statusOverride = null)
    {
        int h = Height(), w = Width();
        var plen = LineEditor.DisplayWidth(LineEditor.PromptText);

        // 하단 예약 영역은 auto-wrap에 기대면(마지막 화면행 wrap이 스크롤 영역을 침범) 깨진다.
        // 프롬프트+버퍼를 폭 w 셀 단위로 직접 분할해 각 행을 절대 좌표로 그린다.
        var rows = SplitByCells(LineEditor.PromptText + buf.ToString(), w);
        var inputRows = rows.Count;

        // 인라인 자동완성(ghost): 입력이 한 줄이고 커서가 끝이며 프롬프트+버퍼+ghost 가 폭에 들어갈 때만
        // 첫 행 버퍼 뒤에 연한 글자로 덧그린다(예약 행수·wrap 계산은 버퍼 기준 그대로 — 레이아웃 안정).
        var ghost = LineEditor.EffectiveGhost(buf, _slash);
        var blen = LineEditor.DisplayWidth(buf.ToString());
        var showGhost = !_shell && ghost.Length > 0 && pos == buf.Length && inputRows == 1
                        && plen + blen + LineEditor.DisplayWidth(ghost) <= w;
        // 레이아웃(위→아래): 입력행(배경색으로 구분) / 상태줄("act mode"). 구분선 없음.
        var reserved = inputRows + 1;
        var scrollBottom = h - reserved;              // 마지막 스크롤 행(1-기반)
        if (scrollBottom < 1)
        {
            scrollBottom = 1;
            reserved = h - 1;
        }

        // 턴 모드에선 스크롤 영역을 고정한다(재설정 금지) — 현재 예약 높이 안에서만 그린다.
        if (_turnMode)
        {
            reserved = _reserved;
            scrollBottom = h - reserved;
            if (scrollBottom < 1) scrollBottom = 1;
            inputRows = Math.Min(inputRows, Math.Max(1, reserved - 1));
        }

        var sb = new StringBuilder();
        if (_turnMode)
        {
            // 커서 저장(출력 커서=영역 하단) + 실제 커서 숨김 — 편집 위치는 가짜 캐럿(반전 블록)으로만 표시해
            // 진짜 커서가 스피너 옆(영역 하단)에 고정돼 보이는 현상을 없앤다.
            sb.Append("\u001b7\u001b[?25l");
        }
        else if (!_installed)
        {
            // 신규 설치(입력 대기 시작): 하단에 reserved 줄 공간 확보 — 화면을 위로 스크롤해
            // 기존 출력은 스크롤백으로 보존하고, 그 빈 자리에 박스를 그린다(직전 출력을 덮지 않게).
            sb.Append($"\x1b[{h};1H");
            for (var k = 0; k < reserved; k++) sb.Append('\n');
            sb.Append($"\x1b[1;{scrollBottom}r");
            _reserved = reserved;
            _installed = true;
        }
        else if (reserved != _reserved)
        {
            // 편집 중 입력 줄 수 변화 → 스크롤 영역 재설정.
            // 박스가 줄면(reserved 감소) 위로 비워진 행이 스크롤 영역에 옛 입력(첫 줄 '❯…' 등)으로
            // 남아 '첫 줄 중복'으로 보인다 → 그 행들을 명시적으로 지운다. (한글처럼 잘 wrap 되는 입력에서 두드러짐)
            if (reserved < _reserved)
            {
                var oldTop = h - _reserved + 1;
                var newTop = h - reserved + 1;
                for (var row = oldTop; row < newTop; row++)
                {
                    sb.Append($"\x1b[{row};1H\x1b[2K");
                }
            }

            sb.Append($"\x1b[1;{scrollBottom}r");
            _reserved = reserved;
        }

        var inputRow0 = scrollBottom + 1;
        var statusRow = inputRow0 + inputRows;

        // 입력행: 각 행을 clear 후 절대 좌표로 직접 출력(auto-wrap 미사용).
        // '!' 셸 모드면 어두운 빨강 배경 + '❯' 숨김(폭 유지 위해 공백 2칸), 아니면 어두운 회색 배경 + 초록 '❯'.
        var shell = _shell;
        var bg = shell ? LineEditor.ShellBg
               : _brainstorm ? $"\x1b[48;5;{_animShade}m"   // 브레인스토밍: 펄스하는 어두운 파랑
               : LineEditor.InputBg;
        for (var i = 0; i < inputRows; i++)
        {
            sb.Append($"\x1b[{inputRow0 + i};1H\x1b[2K");
            if (bg.Length > 0) sb.Append(bg);
            if (i == 0)
            {
                // rows[0]은 프롬프트("❯ ")로 시작하므로 그 뒤(rest)만 버퍼.
                var rest = rows[0].Length >= LineEditor.PromptText.Length
                    ? rows[0][LineEditor.PromptText.Length..]
                    : "";
                if (shell)
                {
                    sb.Append("  ").Append("\x1b[39m").Append(rest);
                }
                else
                {
                    sb.Append("\x1b[32m").Append(LineEditor.PromptText).Append("\x1b[39m").Append(rest);
                    if (showGhost) sb.Append(LineEditor.GhostColor).Append(ghost).Append("\x1b[39m");
                }
            }
            else
            {
                sb.Append("\x1b[39m").Append(rows[i]);
            }
            if (bg.Length > 0) sb.Append("\x1b[K\x1b[0m"); else sb.Append("\x1b[0m");
        }

        // 상태줄(입력창 아래). 구분선 없음.
        sb.Append($"\x1b[{statusRow};1H\x1b[2K").Append(statusOverride ?? _status());

        // 커서를 편집 위치로(절대 좌표) + 커서 표시(입력 차례). 처리 중엔 숨겨져 있다가 여기서 다시 보임.
        // 커서 (행,열)도 그리기와 같은 규칙(SplitByCells, 와이드문자 straddle 반영)으로 계산한다.
        // naive 나눗셈(curOff/w)은 straddle 로 비는 칸을 무시해 커서가 어긋난다.
        var cursorRows = SplitByCells(LineEditor.PromptText + buf.ToString(0, pos), w);
        var curRow = inputRow0 + (cursorRows.Count - 1);
        var curCol = LineEditor.DisplayWidth(cursorRows[^1]) + 1;
        if (_turnMode)
        {
            // 턴 모드: 실제 커서는 출력용(영역 하단)으로 두고, 편집 위치엔 가짜 캐럿(반전 블록)만 찍는다.
            // 마지막에 저장한 출력 커서로 복원(7/8)해 위 영역 스트리밍이 이어지게 한다.
            if (curRow <= inputRow0 + inputRows - 1)
            {
                sb.Append($"\x1b[{curRow};{curCol}H\x1b[7m \x1b[0m");
            }

            sb.Append("\u001b8");   // 출력 커서 복원
        }
        else
        {
            sb.Append($"\x1b[{curRow};{curCol}H").Append("\x1b[?25h");
        }

        lock (_drawLock)
        {
            Console.Write(sb.ToString());
        }
    }

    // 입력 확정: 스크롤 영역을 해제해 턴 동안 '일반 터미널'로 되돌린다(→ 마우스휠 네이티브 스크롤백 정상).
    // 하단 박스를 지우고 입력한 명령을 일반 흐름으로 echo. 하단 고정은 다음 ReadLine 의 Draw 가 다시 세운다.
    private void SubmitAndTeardown(string text, bool shell = false)
    {
        var boxTop = Math.Max(1, Height() - _reserved + 1);   // 현재 박스(상단 라인)가 시작하는 행
        var sb = new StringBuilder();
        sb.Append("\x1b[r");                                   // 스크롤 영역 해제(전체 화면 정상)
        sb.Append($"\x1b[{boxTop};1H\x1b[J");                  // 박스 있던 자리부터 이하 전체 지움
        sb.Append("\x1b[?25h");                                // 커서 표시
        if (!string.IsNullOrEmpty(text))                      // 빈 텍스트(예: '?')는 에코 없이 도크만 해제
        {
            // 채팅처럼 우측 정렬 버블로 echo(일반 흐름 — 이후 출력이 정상 스크롤).
            sb.Append(UserBubble.Render(text, Width(), shell));
            sb.Append('\n');
        }
        Console.Write(sb.ToString());
        _installed = false;
        _reserved = 0;
    }

    /// <summary>
    /// 확정 명령을 스크롤 영역 위로 echo 하되 <b>composer 는 유지</b>하고 턴 모드로 전환한다(고정 입력창).
    /// 스크롤 영역은 그대로 두고, 명령을 영역 하단에 흘려보낸 뒤 출력 커서를 영역 하단에 park 한다 —
    /// 이후 스트리밍 출력이 composer 위에서 스크롤된다. 입력 버퍼는 비우고 composer 를 다시 그린다.
    /// </summary>
    public void KeepComposerForTurn(string text, bool shell = false)
    {
        var h = Height();
        var scrollBottom = Math.Max(1, h - _reserved);
        // 채팅처럼 우측 정렬 버블로 echo(스크롤 영역 폭 기준).
        var echo = UserBubble.Render(text, Width(), shell);

        lock (_drawLock)
        {
            // 영역 하단으로 이동 → 버블 echo(자체 개행 포함) → 커서를 빈 영역 하단에 park.
            // 스피너(ActivityRow = 영역 하단)가 매 프레임 2K 로 그 줄을 지우므로 echo 가 park 줄에
            // 걸치지 않게 버블 뒤 개행 하나를 더 둔다.
            Console.Write($"\x1b[{scrollBottom};1H\r\n{echo}\r\n");
        }

        _buf.Clear();
        _pos = 0;
        _shell = false;
        _turnMode = true;
        Draw(_buf, _pos);   // 턴 모드로 composer 재그림(save/restore)
    }

    /// <summary>턴 종료 — 턴 모드 해제. 다음 ReadLine 의 Draw 가 실제 커서로 정상 렌더한다.</summary>
    public void EndTurnMode() => _turnMode = false;

    /// <summary>턴 중 여부(ReplApp 이 이벤트 라우팅 판단에 사용).</summary>
    public bool InTurn => _turnMode;

    /// <summary>턴 중 composer 를 다시 그린다(외부 상태줄 변화 등). save/restore 로 출력 커서 보존.</summary>
    public void RedrawInTurn() { if (_turnMode) Draw(_buf, _pos); }

    /// <summary>턴 중 스피너/활동 표시를 그릴 행(입력창 바로 위 = 스크롤 영역 마지막 줄). 절대좌표.</summary>
    public int ActivityRow => Math.Max(1, Height() - _reserved);

    /// <summary>현재 입력 초안 텍스트.</summary>
    public string CurrentText => _buf.ToString();

    /// <summary>초안을 비우고 composer 를 다시 그린다(턴 중 Enter 로 큐에 넣은 뒤 호출).</summary>
    public void ClearDraft() { _buf.Clear(); _pos = 0; Draw(_buf, _pos); }

    /// <summary>
    /// 하단 고정 입력 한 줄 읽기. 반환 규칙은 LineEditor.ReadLine 과 동일(null=EOF/quit).
    /// cycleMode: Shift+Tab 시 모드 토글 후 새 상태줄 문자열 반환.
    /// </summary>
    public string? ReadLine(
        IReadOnlyList<string> history,
        IReadOnlyList<string> slashCommands,
        Func<string>? cycleMode,
        bool brainstorm = false,
        string? initialText = null)
    {
        _slash = slashCommands;
        _shell = false;
        _brainstorm = brainstorm;
        _animTick = 0;
        _animShade = BrainstormPalette[0];
        _cycleMode = cycleMode;
        _hist = history;
        _turnMode = false;   // 프롬프트 편집: 실제 커서로 정상 렌더
        // composer 가 이미 설치돼 있고 새 초기값이 없으면(턴 종료 후 이어짐) 진행 중이던 초안을 보존한다.
        if (!_installed || initialText != null)
        {
            _buf.Clear();
            _buf.Append(initialText ?? string.Empty);
            _pos = _buf.Length;
        }
        _histIdx = history.Count;
        _savedCurrent = "";
        _lastW = Width();
        _lastH = Height();
        Draw(_buf, _pos);

        // bracketed paste·포커스·마우스 VT 기능은 TerminalSession 이 세션 단위로 켜둔다(입력 층 재작성).
        while (true)
        {
            var ev = ReadEventWithResize(_buf, _pos);
            switch (HandleEvent(ev))
            {
                case ComposerOutcome.Submit:
                {
                    var content = _buf.ToString();
                    // 슬래시 명령(/...)은 모델 턴이 아니라 조기 처리(피커 등 일반 터미널 필요)라 composer 를
                    // 해제한다. 실제 모델 턴만 composer 를 유지(고정 입력창)한다.
                    if (content.TrimStart().StartsWith('/'))
                    {
                        SubmitAndTeardown(content);
                    }
                    else
                    {
                        KeepComposerForTurn(content);
                    }
                    return content;
                }
                case ComposerOutcome.SubmitShell:
                {
                    var content = _buf.ToString();
                    SubmitAndTeardown(content, shell: true);   // '!' 셸은 일반 터미널에서 실행
                    return "!" + content;
                }
                case ComposerOutcome.Keymap:
                    SubmitAndTeardown("");
                    return "?";
                case ComposerOutcome.Quit:
                    return null;
                case ComposerOutcome.None:
                default:
                    continue;
            }
        }
    }

    /// <summary>
    /// 입력 이벤트 하나를 처리해 _buf/_pos 를 편집하고 다시 그린다. 프롬프트 루프와 턴 중 편집이
    /// <b>같은</b> 처리를 공유해 입력창이 항상 동일하게 동작한다. 확정/종료류만 ComposerOutcome 로 알린다.
    /// </summary>
    public ComposerOutcome HandleEvent(Input.InputEvent ev)
    {
        if (ev is Input.PasteEvent pe)
        {
            PasteStore.Insert(_buf, ref _pos, pe.Text);
            Draw(_buf, _pos);
            return ComposerOutcome.None;
        }

        if (ev is Input.CancelEvent)
        {
            if (_buf.Length > 0) { _buf.Clear(); _pos = 0; Draw(_buf, _pos); return ComposerOutcome.None; }
            return ComposerOutcome.Quit;
        }

        if (ev is not Input.KeyEvent keyEvent)
        {
            return ComposerOutcome.None;   // Mouse/Focus 등 무시
        }

        var key = keyEvent.Key;

        if (key.Key == ConsoleKey.Enter || key.KeyChar == '\r' || key.KeyChar == '\n')
        {
            if (_buf.ToString().Trim().Length == 0)
            {
                return ComposerOutcome.None; // 빈 입력 무시
            }

            return _shell ? ComposerOutcome.SubmitShell : ComposerOutcome.Submit;
        }

        switch (key.Key)
        {
            case ConsoleKey.Backspace:
                if (_shell && _buf.Length == 0)
                {
                    _shell = false;
                    Draw(_buf, _pos);
                    break;
                }
                if (_pos > 0)
                {
                    var n = PasteStore.PlaceholderLengthEndingAt(_buf.ToString(), _pos);
                    var del = n > 0 ? n : 1;
                    _buf.Remove(_pos - del, del); _pos -= del; Draw(_buf, _pos);
                }
                break;
            case ConsoleKey.Delete:
                if (_pos < _buf.Length) { _buf.Remove(_pos, 1); Draw(_buf, _pos); }
                break;
            case ConsoleKey.LeftArrow:
                if (_pos > 0) { _pos--; Draw(_buf, _pos); }
                break;
            case ConsoleKey.RightArrow:
                if (_pos < _buf.Length) { _pos++; Draw(_buf, _pos); }
                break;
            case ConsoleKey.Home:
                _pos = 0; Draw(_buf, _pos);
                break;
            case ConsoleKey.End:
                _pos = _buf.Length; Draw(_buf, _pos);
                break;
            case ConsoleKey.UpArrow:
                if (_histIdx > 0)
                {
                    if (_histIdx == _hist.Count) _savedCurrent = _buf.ToString();
                    _histIdx--;
                    SetBuffer(_buf, ref _pos, _hist[_histIdx]);
                    Draw(_buf, _pos);
                }
                break;
            case ConsoleKey.DownArrow:
                if (_histIdx < _hist.Count)
                {
                    _histIdx++;
                    SetBuffer(_buf, ref _pos, _histIdx == _hist.Count ? _savedCurrent : _hist[_histIdx]);
                    Draw(_buf, _pos);
                }
                break;
            case ConsoleKey.Tab:
                if (key.Modifiers.HasFlag(ConsoleModifiers.Shift))
                {
                    if (_cycleMode is not null)
                    {
                        Draw(_buf, _pos, _cycleMode());
                    }
                    break;
                }
                if (LineEditor.TryAcceptSeed(_buf, ref _pos) || TryComplete(_buf, ref _pos, _slash))
                {
                    Draw(_buf, _pos);
                }
                break;
            default:
                if (key.KeyChar == '\u0004') // Ctrl+D
                {
                    if (_buf.Length == 0) { return ComposerOutcome.Quit; }
                    break;
                }
                if (key.KeyChar == '\u0015') // Ctrl+U
                {
                    _buf.Clear(); _pos = 0; Draw(_buf, _pos);
                    break;
                }
                if (_buf.Length == 0 && !_shell && key.KeyChar == '!')
                {
                    _shell = true;
                    Draw(_buf, _pos);
                    break;
                }
                if (_buf.Length == 0 && !_shell && key.KeyChar == '?')
                {
                    return ComposerOutcome.Keymap;
                }
                if (!char.IsControl(key.KeyChar))
                {
                    _buf.Insert(_pos, key.KeyChar); _pos++;
                    Draw(_buf, _pos);
                }
                break;
        }

        return ComposerOutcome.None;
    }

    // 이벤트를 기다리되, 대기 중 터미널 크기가 바뀌면 도크를 재설치한다(리사이즈 잔상 제거).
    // 공용 리더(raw+VtParser)가 없으면(세션 밖) Console 키를 감싸 폴백.
    private Input.InputEvent ReadEventWithResize(StringBuilder buf, int pos)
    {
        var input = Input.TerminalInput.Shared;
        if (input is null)
        {
            return new Input.KeyEvent(Console.ReadKey(intercept: true));
        }

        while (true)
        {
            // ResizePollMs 만큼 이벤트를 기다리고, 없으면 리사이즈/펄스를 확인하고 다시 대기.
            // (ESC 타임아웃 확정은 TryReadEvent 안에서 처리된다.)
            if (input.TryReadEvent(ResizePollMs) is { } ev)
            {
                return ev;
            }

            int w = Width(), h = Height();
            if (w != _lastW || h != _lastH)
            {
                OnResize(buf, pos);   // _lastW/_lastH 는 OnResize 가 옛 크기로 잔상 계산 뒤 갱신
                _lastW = w;
                _lastH = h;
            }
            else if (_brainstorm)
            {
                // 브레인스토밍 펄스: 프레임이 바뀌는 순간에만 입력행을 다시 그린다(음영 변경).
                _animTick++;
                var shade = BrainstormPalette[(_animTick / AnimTicksPerFrame) % BrainstormPalette.Length];
                if (shade != _animShade)
                {
                    _animShade = shade;
                    Draw(buf, pos);
                }
            }
        }
    }

    // 리사이즈 처리: 터미널이 DECSTBM 영역을 리셋해 이전 입력창이 화면 중간에 잔상으로 남는다.
    // 지우는 방식이 핵심: ED(2J/ED0) 는 데스크톱 터미널이 창이 커질 때 스크롤백에서 끌어올린 대화
    // 줄까지 지워 복구 불가로 만든다. 대신 옛 composer 가 있던 행들만 행 단위 지움(2K)을 쓴다.
    // 리사이즈 후 옛 하단 내용은 새 하단에 붙는다(성장=위에 줄 추가, 축소=아래 줄은 스크롤백으로
    // 밀림) — 즉 옛 좌표를 (newH - oldH) 만큼 평행이동한 행이 옛 composer 의 새 위치다. 거기에
    // reflow 여유 마진을 얹어 지운다.
    private void OnResize(StringBuilder buf, int pos)
    {
        var h = Height();
        var delta = h - _lastH;                                 // 리사이즈로 인한 세로 이동량
        var first = Math.Max(1, _lastH + delta - _reserved + 1 - 4);   // 마진 포함 옛 composer 의 새 상단
        var last = Math.Min(h, _lastH + delta);                 // 옛 화면 하단의 새 위치
        var sb = new StringBuilder("\x1b[r");                  // 스크롤 영역 해제
        for (var row = first; row <= last; row++)
        {
            sb.Append($"\x1b[{row};1H\x1b[2K");
        }

        Console.Write(sb.ToString());
        _installed = false;
        _reserved = 0;
        _shrunkRecently = false;   // 프롬프트 경로 재설치 — 잔상 플래그 초기화
        Draw(buf, pos);   // Draw 가 새 크기로 재설치(스크롤로 예약 줄 확보 + composer 재그림)
    }

    /// <summary>
    /// 턴 중 리사이즈 처리(ESC 워처 40ms 폴에서 호출). 턴 모드에선 Draw 가 스크롤 영역을 재설정하지
    /// 못하므로 잔상만 지우고 composer 를 다시 그린다. 크기 변화가 없으면 아무것도 안 한다.
    /// </summary>
    public void HandleResizeInTurn()
    {
        int w = Width(), h = Height();
        if (w == _lastW && h == _lastH)
        {
            return;
        }

        // 옛 composer 잔상을 (newH-oldH) 평행이동 위치에서 행 단위로 지운다(ED 금지 — 스크롤백 보존).
        var delta = h - _lastH;
        var first = Math.Max(1, _lastH + delta - _reserved + 1 - 4);
        var last = Math.Min(h, _lastH + delta);
        // 축소했다가 다시 키우면, 축소 때 스크롤백으로 밀려난 옛 composer 행이 화면으로 되돌아와
        // 새 입력창 바로 위에 중복 잔상으로 남는다(상태줄 2줄). 커진 만큼 위쪽도 같이 지운다.
        if (delta > 0 && _shrunkRecently)
        {
            first = Math.Max(1, first - delta);
        }

        _shrunkRecently = delta < 0 || _shrunkRecently;
        if (delta > 0)
        {
            _shrunkRecently = false;   // 커진 시점의 되돌아온 잔상까지 지웠으니 플래그 해제
        }

        var sb = new StringBuilder();
        for (var row = first; row <= last; row++)
        {
            sb.Append($"\x1b[{row};1H\x1b[2K");
        }

        sb.Append($"\x1b[1;{Math.Max(1, h - _reserved)}r");   // 새 크기로 스크롤 영역 재설정
        lock (_drawLock)
        {
            Console.Write(sb.ToString());
        }

        _lastW = w;
        _lastH = h;
        Draw(_buf, _pos);   // 턴 모드 Draw(save/restore — 출력 커서 보존)
    }

    // 문자열을 표시폭 w 셀 단위로 분할(넓은 문자를 경계에서 쪼개지 않음). 최소 1개 행 반환.
    private static List<string> SplitByCells(string s, int w)
    {
        if (w < 1) w = 1;
        var rows = new List<string>();
        var cur = new StringBuilder();
        var cw = 0;
        foreach (var ch in s)
        {
            var cwid = LineEditor.CharWidth(ch);
            if (cw + cwid > w)
            {
                rows.Add(cur.ToString());
                cur.Clear();
                cw = 0;
            }
            cur.Append(ch);
            cw += cwid;
        }
        rows.Add(cur.ToString());
        return rows;
    }

    // ── 유틸 (docked 전용, 목록 출력 없음) ─────────────────────────────────────
    private static void SetBuffer(StringBuilder buf, ref int pos, string text)
    {
        buf.Clear(); buf.Append(text); pos = buf.Length;
    }

    // Tab 자동완성: '/' 명령 또는 '@' 파일 멘션 확정. LineEditor 와 동일 규칙 공유.
    private static bool TryComplete(StringBuilder buf, ref int pos, IReadOnlyList<string> slash)
        => LineEditor.AcceptCompletion(buf, ref pos, slash);
}
