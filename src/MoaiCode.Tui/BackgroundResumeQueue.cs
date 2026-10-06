using System.Collections.Concurrent;
using MoaiCode.Tools.Bash;

namespace MoaiCode.Tui;

/// <summary>
/// 백그라운드 셸 완료 통보 큐 + 자동 재개 턴 연속 상한. Registry 의 스레드풀 콜백(Enqueue)을 입력 스레드
/// (HasPending 폴링·Drain)로 넘긴다. 모델이 이미 최종 상태를 아는 셸(직접 kill, 종료 후 BashOutput 으로
/// 끝까지 읽음)은 통보에서 뺀다. 사용자 입력 없이 재개 턴이 연쇄(재개 턴이 또 셸을 띄움)하지 않게
/// 연속 <see cref="MaxConsecutive"/> 회에서 멈추고, 사용자 입력이 들어오면 다시 센다.
/// </summary>
/// <remarks>
/// Channel 대신 ConcurrentQueue 를 쓴다 — Channel.CreateUnbounded(SingleReader = true) 는 단일 소비자
/// 구현을 골라 Reader.CanCount=false(Count 미지원)라 폴링 감지가 불가했다(1.2.21 idle wake 불발의 원인).
/// </remarks>
internal sealed class BackgroundResumeQueue
{
    /// <summary>사용자 입력 없이 이어지는 자동 재개 턴의 최대 연속 횟수.</summary>
    public const int MaxConsecutive = 3;

    private readonly ConcurrentQueue<BackgroundShellFinished> _queue = new();
    private readonly Func<string, bool> _acknowledged;
    private int _streak;

    /// <param name="acknowledged">셸 id → 모델이 이미 최종 상태를 아는가(<see cref="BackgroundShell.Acknowledged"/>).</param>
    public BackgroundResumeQueue(Func<string, bool> acknowledged) => _acknowledged = acknowledged;

    /// <summary>완료 통보 적재(스레드풀 스레드에서 호출).</summary>
    public void Enqueue(BackgroundShellFinished finished) => _queue.Enqueue(finished);

    /// <summary>보고할 완료가 있는가 — 입력 대기 폴링(40ms)마다 호출된다. 이미 아는 셸만 있으면 깨우지 않는다.</summary>
    public bool HasPending => !_queue.IsEmpty && _queue.Any(f => !_acknowledged(f.Id));   // 비었으면 스냅샷 생략

    /// <summary>쌓인 통보를 전부 꺼내 보고할 것만 돌려준다(여러 개면 재개 턴 하나로 합친다).</summary>
    public List<BackgroundShellFinished> Drain()
    {
        var drained = new List<BackgroundShellFinished>();
        while (_queue.TryDequeue(out var fin))
        {
            if (!_acknowledged(fin.Id))
            {
                drained.Add(fin);
            }
        }

        return drained;
    }

    /// <summary>재개 턴을 띄워도 되면 true(연속 횟수 1 증가). 상한에 닿았으면 false.</summary>
    public bool TryBeginResume()
    {
        if (_streak >= MaxConsecutive)
        {
            return false;
        }

        _streak++;
        return true;
    }

    /// <summary>사용자 입력 도착 — 연속 횟수를 다시 센다.</summary>
    public void OnUserInput() => _streak = 0;
}
