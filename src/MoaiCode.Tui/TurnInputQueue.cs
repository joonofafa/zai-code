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

    /// <summary>미확정 줄이 남아 있으면 확정 큐로 넘긴다(턴 종료 시 호출).</summary>
    public void CommitPartial()
    {
        lock (_lock)
        {
            CommitLocked();
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
