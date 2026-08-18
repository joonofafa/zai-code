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

    private void CommitLocked()
    {
        var t = _line.ToString().Trim();
        _line.Clear();
        if (t.Length > 0)
        {
            _messages.Enqueue(t);
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
