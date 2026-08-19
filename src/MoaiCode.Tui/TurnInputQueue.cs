using System.Text;

namespace MoaiCode.Tui;

/// <summary>
/// 타입어헤드 큐. 에이전트가 턴을 처리하는 동안(ESC 워처가 stdin 을 폴링) 사용자가 친 키를 모아,
/// 턴이 끝나면 다음 프롬프트로 순차 제출한다. 워처 스레드(쓰기)와 메인 스레드(스피너 표시/드레인, 읽기)가
/// 함께 접근하므로 잠금으로 보호한다.
/// </summary>
internal sealed class TurnInputQueue
{
    private readonly object _lock = new();
    private readonly StringBuilder _line = new();
    private readonly Queue<string> _messages = new();

    // 마지막으로 큐잉된 붙여넣기/메시지 미리보기(하단 바 '↳ Queued ❯ …' 표시용).
    // 만료 시각과 함께 저장하고 DrawBar 쪽에서 만료 검사한다. 큐 입력 상황에서
    // Q:n 숫자만 바뀌면 붙여넣어졌는지 알 길이 없어 잠깐 노출한다.
    private string _flashPreview = string.Empty;
    private DateTime _flashUntil = DateTime.MinValue;

    /// <summary>워처가 읽은 키 1개를 반영. Enter=현재 줄 확정, Backspace=한 글자 삭제, 인쇄가능=추가.</summary>
    public void Feed(ConsoleKeyInfo k)
    {
        lock (_lock)
        {
            if (k.Key == ConsoleKey.Enter)
            {
                CommitLocked();
            }
            else if (k.Key == ConsoleKey.Backspace)
            {
                if (_line.Length > 0)
                {
                    _line.Remove(_line.Length - 1, 1);
                }
            }
            else if (!char.IsControl(k.KeyChar))
            {
                _line.Append(k.KeyChar);
            }
        }
    }

    /// <summary>
    /// 턴 중 붙여넣은 원문 전체를 '한 개' 메시지로 커밋한다. 줄바꿈마다 Enter 로 쪼개 큐잉되면
    /// 여러 메시지로 흩어져 홍수가 나기 때문(브리지 로그·화면 난립 문제). 치다 만 줄이 있으면
    /// 먼저 커밋해 두고 붙여넣기는 그다음 메시지로 붙인다.
    /// </summary>
    public void FeedPaste(string text)
    {
        var t = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim('\n');
        if (t.Length == 0)
        {
            return;
        }

        lock (_lock)
        {
            CommitLocked();
            _messages.Enqueue(t);
            SetFlashLocked(PreviewOf(t));
        }
    }

    /// <summary>미리보기: 첫 줄을 짧게. 여러 줄이면 '[+N줄]'을 단다(표식과 유사한 형태).</summary>
    private static string PreviewOf(string text)
    {
        var nl = text.IndexOf('\n');
        var first = nl < 0 ? text : text[..nl];
        if (first.Length > 40)
        {
            first = first[..40] + "…";
        }

        if (nl >= 0)
        {
            var lines = 1;
            foreach (var c in text)
            {
                if (c == '\n')
                {
                    lines++;
                }
            }

            first += $" [+{lines} lines]";
        }

        return first;
    }

    private void SetFlashLocked(string preview)
    {
        _flashPreview = preview;
        _flashUntil = DateTime.UtcNow.AddSeconds(2);
    }

    /// <summary>
    /// 활성 플래시 미리보기(만료 전이면). 읽는 시점에 만료됐으면 지우고 null.
    /// 하단 바가 주기적으로 다시 그려지므로 만료 후엔 자연히 사라진다.
    /// </summary>
    public string? TakeFlash()
    {
        lock (_lock)
        {
            if (DateTime.UtcNow >= _flashUntil)
            {
                _flashPreview = string.Empty;
                _flashUntil = DateTime.MinValue;
                return null;
            }

            return _flashPreview;
        }
    }

    /// <summary>Enter 로 확정 커밋된 보통 한 줄도 플래시로 잠깐 보여준다(북여넣기가 아닐 때도 피드백).</summary>
    private void CommitLocked()
    {
        var t = _line.ToString().Trim();
        _line.Clear();
        if (t.Length > 0)
        {
            _messages.Enqueue(t);
            SetFlashLocked(PreviewOf(t));
        }
    }

    /// <summary>턴 중 Shift+Tab 모드 전환 신호 예약 — 드레인 루프가 만나면 CycleMode 를 실행한다.</summary>
    public void EnqueueModeCycle()
    {
        lock (_lock)
        {
            CommitLocked();
            _messages.Enqueue(LineEditor.CycleModeSignal);
        }
    }

    /// <summary>현재 입력 중(미확정) 줄 — 스피너 라인에 표시용.</summary>
    public string CurrentLine
    {
        get { lock (_lock) { return _line.ToString(); } }
    }

    /// <summary>확정 대기 중인(큐잉된) 메시지 개수 — 하단 바 (Q:N) 표시용. 미확정 줄은 제외.</summary>
    public int Count
    {
        get { lock (_lock) { return _messages.Count; } }
    }

    /// <summary>
    /// 미확정(엔터 전) 줄을 꺼내 비운다. 턴이 끝나면 이건 제출하지 않고 다음 프롬프트의 입력
    /// 버퍼로 되살린다 — 치던 중에 턴이 끝났다고 멋대로 전송되면 안 되기 때문.
    /// </summary>
    public string TakePartial()
    {
        lock (_lock)
        {
            var t = _line.ToString();
            _line.Clear();
            return t;
        }
    }

    public bool TryDequeue(out string message)
    {
        lock (_lock)
        {
            if (_messages.Count > 0)
            {
                message = _messages.Dequeue();
                return true;
            }

            message = string.Empty;
            return false;
        }
    }
}
