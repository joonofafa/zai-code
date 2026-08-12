namespace MoaiCode.Tools.Tasks;

public enum TaskStatus { Pending, InProgress, Completed }

/// <summary>페이즈 진행 상태 — Done(전부 완료) / Active(현재 진행 중인 페이즈) / Pending(아직 대기).</summary>
public enum PhaseStatus { Pending, Active, Done }

/// <summary>코딩 난이도 — 모델 라우팅 티어(하/중/상). 티어별 모델은 CLI 가 env/설정으로 해소.</summary>
public enum Difficulty { Low, Mid, High }

public sealed record TaskItem(
    string Id, string Subject, TaskStatus Status, int Phase = 0, Difficulty Difficulty = Difficulty.Mid);

/// <summary>페이즈 단위 뷰(번호·제목·소속 태스크·상태). Phase==0 태스크(평면)는 페이즈에 포함하지 않는다.</summary>
public sealed record PhaseView(int Number, string Title, IReadOnlyList<TaskItem> Tasks, PhaseStatus Status);

/// <summary>PlanCreate 입력용 페이즈 정의(제목 + (태스크, 난이도) 목록).</summary>
public sealed record PhasePlan(string Title, IReadOnlyList<(string Subject, Difficulty Diff)> Tasks);

/// <summary>세션 범위 인메모리 태스크 저장소. 평면 태스크(Phase=0) + 선형 페이즈(Phase>=1) + 난이도 티어를 지원.</summary>
public sealed class TaskStore
{
    private readonly object _lock = new();
    private readonly List<TaskItem> _items = new();
    private readonly Dictionary<int, string> _phaseTitles = new();
    private int _seq;

    /// <summary>"low/하/easy" → Low, "high/상/hard/complex" → High, 그 외 → Mid.</summary>
    public static Difficulty ParseDifficulty(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "low" or "하" or "easy" or "l" => Difficulty.Low,
        "high" or "상" or "hard" or "complex" or "h" => Difficulty.High,
        _ => Difficulty.Mid,
    };

    public TaskItem Add(string subject, Difficulty diff = Difficulty.Mid) => AddPhased(0, subject, diff);

    public TaskItem AddPhased(int phase, string subject, Difficulty diff = Difficulty.Mid)
    {
        lock (_lock)
        {
            var item = new TaskItem((++_seq).ToString(), subject, TaskStatus.Pending, phase, diff);
            _items.Add(item);
            return item;
        }
    }

    /// <summary>플랜 전체 교체: 기존 태스크/페이즈 제거 후 1..N 페이즈드 태스크(난이도 포함)로 적재.</summary>
    public void SetPlan(IReadOnlyList<PhasePlan> phases)
    {
        lock (_lock)
        {
            _items.Clear();
            _phaseTitles.Clear();
            _seq = 0;
            for (var p = 0; p < phases.Count; p++)
            {
                var num = p + 1;
                _phaseTitles[num] = phases[p].Title;
                foreach (var (subject, diff) in phases[p].Tasks)
                {
                    _items.Add(new TaskItem((++_seq).ToString(), subject, TaskStatus.Pending, num, diff));
                }
            }
        }
    }

    public IReadOnlyList<TaskItem> All()
    {
        lock (_lock)
        {
            return _items.ToList();
        }
    }

    public bool Update(string id, TaskStatus status)
    {
        lock (_lock)
        {
            var idx = _items.FindIndex(t => t.Id == id);
            if (idx < 0)
            {
                return false;
            }

            _items[idx] = _items[idx] with { Status = status };
            return true;
        }
    }

    /// <summary>현재 진행(in_progress) 중인 태스크의 난이도. 없거나 여럿이면 첫 in_progress, 아무것도 없으면 null.</summary>
    public Difficulty? CurrentTaskDifficulty()
    {
        lock (_lock)
        {
            var t = _items.FirstOrDefault(x => x.Status == TaskStatus.InProgress);
            return t is null ? null : t.Difficulty;
        }
    }

    /// <summary>현재 진행 중인 태스크의 난이도를 한 티어 올린다(Low→Mid→High). 올렸으면 새 난이도, 이미 High/없으면 null.</summary>
    public Difficulty? EscalateCurrent()
    {
        lock (_lock)
        {
            var idx = _items.FindIndex(x => x.Status == TaskStatus.InProgress);
            if (idx < 0 || _items[idx].Difficulty == Difficulty.High)
            {
                return null;
            }

            var next = _items[idx].Difficulty == Difficulty.Low ? Difficulty.Mid : Difficulty.High;
            _items[idx] = _items[idx] with { Difficulty = next };
            return next;
        }
    }

    /// <summary>페이즈별 뷰(번호 오름차순). 현재 진행 페이즈=Active, 그 이전=Done, 이후=Pending.</summary>
    public IReadOnlyList<PhaseView> Phases()
    {
        lock (_lock)
        {
            var cur = CurrentPhaseLocked();
            var views = new List<PhaseView>();
            foreach (var g in _items.Where(t => t.Phase > 0).GroupBy(t => t.Phase).OrderBy(g => g.Key))
            {
                var tasks = g.ToList();
                var status = tasks.All(t => t.Status == TaskStatus.Completed)
                    ? PhaseStatus.Done
                    : (cur is not null && g.Key == cur ? PhaseStatus.Active : PhaseStatus.Pending);
                views.Add(new PhaseView(g.Key, _phaseTitles.GetValueOrDefault(g.Key, $"Phase {g.Key}"), tasks, status));
            }

            return views;
        }
    }

    /// <summary>완료되지 않은 태스크가 있는 가장 낮은 페이즈 번호(페이즈 없거나 전부 완료면 null).</summary>
    public int? CurrentPhase()
    {
        lock (_lock)
        {
            return CurrentPhaseLocked();
        }
    }

    private int? CurrentPhaseLocked()
    {
        var incomplete = _items
            .Where(t => t.Phase > 0 && t.Status != TaskStatus.Completed)
            .Select(t => t.Phase)
            .ToList();
        return incomplete.Count > 0 ? incomplete.Min() : null;
    }
}
