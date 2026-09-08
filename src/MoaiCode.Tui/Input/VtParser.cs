using System.Text;

namespace MoaiCode.Tui.Input;

/// <summary>
/// 터미널 raw 바이트 → <see cref="InputEvent"/> 증분 상태머신. 콘솔·플랫폼 비의존(순수) 이라 전량 유닛테스트한다.
///
/// <para><see cref="Push"/> 로 바이트 청크를 먹이면 완성된 이벤트만 돌려주고, 읽기 경계에 걸쳐 쪼개진
/// 시퀀스(예: ESC[ 까지만 온 경우, UTF-8 선두바이트만 온 경우)는 내부 버퍼에 남겨 다음 청크와 합친다.</para>
///
/// <para>단독 ESC 는 뒤에 시퀀스가 이어질 수 있어 즉시 확정하지 않는다 — 짧은 시간 뒤 더 안 오면
/// 리더가 <see cref="Flush"/> 를 불러 Escape 키로 확정한다(ESC 타임아웃 관용).</para>
///
/// 지원: 인쇄 룬(UTF-8), 화살표·Home/End·Ins/Del·PgUp/Dn·Tab·Backspace·Enter,
/// 수식자(CSI 1;m), F1–F4(SS3), Alt+키(ESC 접두), bracketed paste(200~/201~),
/// 포커스(ESC[I/O), 마우스(SGR 1006), Ctrl+letter(0x01–0x1a), Ctrl+C(0x03).
/// </summary>
public sealed class VtParser
{
    private const byte Esc = 0x1b;
    private const int MaxPasteBytes = 4_000_000;   // 종료 마커 미수신 시 무한 성장 방지(기존 Paste.cs 상한과 동일)

    private static readonly byte[] PasteEnd = "[201~"u8.ToArray();

    private readonly List<byte> _buf = new();
    private bool _inPaste;
    private readonly List<byte> _paste = new();

    /// <summary>바이트 청크를 먹이고 이번에 확정된 이벤트들을 반환. 미완성 시퀀스는 내부 보존.</summary>
    public IReadOnlyList<InputEvent> Push(ReadOnlySpan<byte> data)
    {
        var outEvents = new List<InputEvent>();
        foreach (var b in data)
        {
            _buf.Add(b);
        }

        Drain(outEvents);
        return outEvents;
    }

    /// <summary>대기 중인 게 단독 ESC 뿐인가(리더가 ESC 타임아웃을 걸지 판단). paste 중이면 아니다.</summary>
    public bool PendingIsEscape => !_inPaste && _buf.Count == 1 && _buf[0] == Esc;

    /// <summary>더 이상 바이트가 안 온다고 판단될 때(ESC 타임아웃) 호출 — 대기 중 단독 ESC 를 Escape 로 확정.</summary>
    public IReadOnlyList<InputEvent> Flush()
    {
        var outEvents = new List<InputEvent>();
        if (!_inPaste && _buf.Count == 1 && _buf[0] == Esc)
        {
            _buf.Clear();
            outEvents.Add(Key('\u001b', ConsoleKey.Escape));
        }

        return outEvents;
    }

    private void Drain(List<InputEvent> outEvents)
    {
        while (_buf.Count > 0)
        {
            if (_inPaste)
            {
                if (!DrainPaste(outEvents))
                {
                    return; // 종료 마커 대기
                }

                continue;
            }

            var consumed = TryParseOne(outEvents);
            if (consumed == 0)
            {
                return; // 미완성 — 다음 청크 대기
            }

            _buf.RemoveRange(0, consumed);
        }
    }

    // paste 모드: 버퍼에서 종료 마커(ESC[201~)를 찾으면 그 앞까지를 본문으로 확정.
    private bool DrainPaste(List<InputEvent> outEvents)
    {
        var idx = IndexOf(_buf, PasteEnd, 0);
        if (idx >= 0)
        {
            for (var i = 0; i < idx; i++)
            {
                _paste.Add(_buf[i]);
            }

            _buf.RemoveRange(0, idx + PasteEnd.Length);
            outEvents.Add(new PasteEvent(Encoding.UTF8.GetString(_paste.ToArray())));
            _paste.Clear();
            _inPaste = false;
            return true;
        }

        // 종료 마커가 아직 없다: 마커가 청크 경계에 쪼개졌을 수 있으니 마지막 (len-1) 바이트만 남기고
        // 안전한 앞부분은 본문으로 이월한다(대용량 붙여넣기에서도 버퍼가 무한히 자라지 않게).
        var keep = PasteEnd.Length - 1;
        if (_buf.Count > keep)
        {
            var moved = _buf.Count - keep;
            for (var i = 0; i < moved; i++)
            {
                _paste.Add(_buf[i]);
            }

            _buf.RemoveRange(0, moved);
        }

        if (_paste.Count > MaxPasteBytes)
        {
            outEvents.Add(new PasteEvent(Encoding.UTF8.GetString(_paste.ToArray())));
            _paste.Clear();
            _inPaste = false;
            return true;
        }

        return false;
    }

    // 버퍼 앞에서 토큰 하나를 파싱. 소비한 바이트 수 반환(0 = 미완성, 대기).
    private int TryParseOne(List<InputEvent> outEvents)
    {
        var b0 = _buf[0];

        if (b0 == Esc)
        {
            return ParseEscape(outEvents);
        }

        switch (b0)
        {
            case 0x0d: // CR
                // CRLF 는 Enter 하나로 접는다.
                var extra = _buf.Count > 1 && _buf[1] == 0x0a ? 1 : 0;
                outEvents.Add(Key('\r', ConsoleKey.Enter));
                return 1 + extra;
            case 0x0a: // LF
                outEvents.Add(Key('\r', ConsoleKey.Enter));
                return 1;
            case 0x09:
                outEvents.Add(Key('\t', ConsoleKey.Tab));
                return 1;
            case 0x7f:
            case 0x08:
                outEvents.Add(Key('\b', ConsoleKey.Backspace));
                return 1;
            case 0x03:
                outEvents.Add(new CancelEvent());
                return 1;
        }

        // 그 밖의 제어문자(Ctrl+letter): KeyChar 에 제어코드를 실어 기존 / 검사를 만족시킨다.
        if (b0 < 0x20)
        {
            var letter = (char)('A' + (b0 - 1));   // 0x01→A … 0x1a→Z
            var key = b0 is >= 1 and <= 26 ? (ConsoleKey)letter : 0;
            outEvents.Add(new KeyEvent(new ConsoleKeyInfo((char)b0, key, shift: false, alt: false, control: true)));
            return 1;
        }

        // 인쇄 가능: UTF-8 룬 디코딩(멀티바이트가 덜 왔으면 대기).
        return ParseUtf8(outEvents);
    }

    private int ParseEscape(List<InputEvent> outEvents)
    {
        if (_buf.Count < 2)
        {
            return 0; // ESC 뒤가 아직 — Flush 가 타임아웃 시 Escape 로 확정
        }

        var b1 = _buf[1];
        if (b1 == (byte)'[')
        {
            return ParseCsi(outEvents);
        }

        if (b1 == (byte)'O')
        {
            return ParseSs3(outEvents);
        }

        // ESC + 키 → Alt 수식. (b1 이 멀티바이트 선두면 단순화해 그 바이트만 Alt+문자로 처리.)
        outEvents.Add(new KeyEvent(new ConsoleKeyInfo((char)b1, MapLetterOrDigit((char)b1), shift: false, alt: true, control: false)));
        return 2;
    }

    // CSI: ESC [ <params> <final(0x40–0x7e)>
    private int ParseCsi(List<InputEvent> outEvents)
    {
        var f = 2;
        while (f < _buf.Count && !(_buf[f] >= 0x40 && _buf[f] <= 0x7e))
        {
            f++;
        }

        if (f >= _buf.Count)
        {
            return 0; // final 바이트 아직 — 대기
        }

        var final = (char)_buf[f];
        var paramStr = Encoding.ASCII.GetString(_buf.GetRange(2, f - 2).ToArray());

        // bracketed paste 시작
        if (paramStr == "200" && final == '~')
        {
            _inPaste = true;
            return f + 1;
        }

        // 마우스(SGR 1006): ESC[<b;x;y (M|m)
        if (paramStr.StartsWith('<') && (final == 'M' || final == 'm'))
        {
            EmitMouse(outEvents, paramStr[1..], press: final == 'M');
            return f + 1;
        }

        // 포커스: ESC[I(획득) / ESC[O(상실) — 파라미터 없음
        if (paramStr.Length == 0 && final is 'I' or 'O')
        {
            outEvents.Add(new FocusEvent(final == 'I'));
            return f + 1;
        }

        EmitCsiKey(outEvents, paramStr, final);
        return f + 1;
    }

    // SS3: ESC O <final>
    private int ParseSs3(List<InputEvent> outEvents)
    {
        if (_buf.Count < 3)
        {
            return 0;
        }

        var final = (char)_buf[2];
        var key = final switch
        {
            'A' => ConsoleKey.UpArrow,
            'B' => ConsoleKey.DownArrow,
            'C' => ConsoleKey.RightArrow,
            'D' => ConsoleKey.LeftArrow,
            'H' => ConsoleKey.Home,
            'F' => ConsoleKey.End,
            'P' => ConsoleKey.F1,
            'Q' => ConsoleKey.F2,
            'R' => ConsoleKey.F3,
            'S' => ConsoleKey.F4,
            _ => (ConsoleKey)0,
        };
        if (key != 0)
        {
            outEvents.Add(Key('\0', key));
        }

        return 3;
    }

    // CSI 키 매핑. paramStr 예: "" / "1;5"(수식자) / "3"(Delete) / "3;5"(Ctrl+Delete)
    private void EmitCsiKey(List<InputEvent> outEvents, string paramStr, char final)
    {
        var parts = paramStr.Split(';');
        var mods = parts.Length >= 2 && int.TryParse(parts[1], out var m) ? m : 1;
        var (shift, alt, ctrl) = DecodeMods(mods);

        ConsoleKey key = final switch
        {
            'A' => ConsoleKey.UpArrow,
            'B' => ConsoleKey.DownArrow,
            'C' => ConsoleKey.RightArrow,
            'D' => ConsoleKey.LeftArrow,
            'H' => ConsoleKey.Home,
            'F' => ConsoleKey.End,
            'Z' => ConsoleKey.Tab,   // CBT(back-tab) = Shift+Tab
            '~' => TildeKey(parts.Length > 0 && int.TryParse(parts[0], out var n) ? n : 0),
            _ => (ConsoleKey)0,
        };

        if (final == 'Z')
        {
            shift = true;   // ESC[Z 는 파라미터 없이도 Shift+Tab 을 뜻한다
        }

        if (key != 0)
        {
            outEvents.Add(new KeyEvent(new ConsoleKeyInfo('\0', key, shift, alt, ctrl)));
        }
    }

    private static ConsoleKey TildeKey(int n) => n switch
    {
        1 or 7 => ConsoleKey.Home,
        2 => ConsoleKey.Insert,
        3 => ConsoleKey.Delete,
        4 or 8 => ConsoleKey.End,
        5 => ConsoleKey.PageUp,
        6 => ConsoleKey.PageDown,
        11 => ConsoleKey.F1,
        12 => ConsoleKey.F2,
        13 => ConsoleKey.F3,
        14 => ConsoleKey.F4,
        15 => ConsoleKey.F5,
        _ => (ConsoleKey)0,
    };

    // xterm 수식자 인코딩: mods = 1 + bit(Shift=1, Alt=2, Ctrl=4)
    private static (bool Shift, bool Alt, bool Ctrl) DecodeMods(int mods)
    {
        var m = mods - 1;
        return ((m & 1) != 0, (m & 2) != 0, (m & 4) != 0);
    }

    private void EmitMouse(List<InputEvent> outEvents, string body, bool press)
    {
        var parts = body.Split(';');
        if (parts.Length != 3
            || !int.TryParse(parts[0], out var btn)
            || !int.TryParse(parts[1], out var x)
            || !int.TryParse(parts[2], out var y))
        {
            return;
        }

        // 하위 2비트=버튼, 4=Shift, 8=Alt(Meta), 16=Ctrl, 64=휠.
        var shift = (btn & 4) != 0;
        var alt = (btn & 8) != 0;
        var ctrl = (btn & 16) != 0;
        MouseAction action;
        if ((btn & 64) != 0)
        {
            action = (btn & 1) == 0 ? MouseAction.WheelUp : MouseAction.WheelDown;
        }
        else if ((btn & 32) != 0)
        {
            action = MouseAction.Move;
        }
        else
        {
            action = press ? MouseAction.Press : MouseAction.Release;
        }

        outEvents.Add(new MouseEvent(action, btn & 3, x, y, shift, alt, ctrl));
    }

    private int ParseUtf8(List<InputEvent> outEvents)
    {
        var lead = _buf[0];
        var len = lead switch
        {
            < 0x80 => 1,
            >= 0xc0 and < 0xe0 => 2,
            >= 0xe0 and < 0xf0 => 3,
            >= 0xf0 and < 0xf8 => 4,
            _ => 1, // 잘못된 선두바이트 — 1바이트로 소비(대체문자)
        };

        if (_buf.Count < len)
        {
            return 0; // 멀티바이트가 덜 옴 — 대기
        }

        var s = Encoding.UTF8.GetString(_buf.GetRange(0, len).ToArray());
        foreach (var ch in s)
        {
            // BMP 밖(대리쌍)은 코드유닛 단위로 KeyEvent 를 내보낸다 — StringBuilder 삽입 시 재결합.
            outEvents.Add(new KeyEvent(new ConsoleKeyInfo(ch, KeyOf(ch), shift: false, alt: false, control: false)));
        }

        return len;
    }

    private static ConsoleKey KeyOf(char ch)
        => ch == ' ' ? ConsoleKey.Spacebar : MapLetterOrDigit(ch);

    private static ConsoleKey MapLetterOrDigit(char ch)
    {
        if (ch is >= 'a' and <= 'z')
        {
            return (ConsoleKey)('A' + (ch - 'a'));
        }

        if (ch is >= 'A' and <= 'Z')
        {
            return (ConsoleKey)ch;
        }

        if (ch is >= '0' and <= '9')
        {
            return ConsoleKey.D0 + (ch - '0');
        }

        return 0;
    }

    private static KeyEvent Key(char ch, ConsoleKey key)
        => new(new ConsoleKeyInfo(ch, key, shift: false, alt: false, control: false));

    private static int IndexOf(List<byte> haystack, byte[] needle, int start)
    {
        for (var i = start; i + needle.Length <= haystack.Count; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                return i;
            }
        }

        return -1;
    }
}
