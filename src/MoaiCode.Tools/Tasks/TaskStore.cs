namespace MoaiCode.Tools.Tasks;

public enum TaskStatus { Pending, InProgress, Completed }

public sealed record TaskItem(string Id, string Subject, TaskStatus Status);

/// <summary>세션 범위 인메모리 태스크 저장소 (TS tasks 레지스트리 대응, 단순화).</summary>
public sealed class TaskStore
{
    private readonly object _lock = new();
    private readonly List<TaskItem> _items = new();
    private int _seq;

    public TaskItem Add(string subject)
    {
        lock (_lock)
        {
            var item = new TaskItem((++_seq).ToString(), subject, TaskStatus.Pending);
            _items.Add(item);
            return item;
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
}
