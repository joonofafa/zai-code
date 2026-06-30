using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoaiCode.Core.Tools;

namespace MoaiCode.Tools.Tasks;

internal static class TaskJson
{
    public static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public static TaskStatus ParseStatus(string? s) => s?.ToLowerInvariant() switch
    {
        "in_progress" or "inprogress" => TaskStatus.InProgress,
        "completed" or "done" => TaskStatus.Completed,
        _ => TaskStatus.Pending,
    };
}

/// <summary>태스크 생성.</summary>
public sealed class TaskCreateTool(TaskStore store) : ITool
{
    public string Name => "TaskCreate";

    public string Description => """
        Create a task in the session task list to plan and track multi-step work.

        When to use:
        - The request needs 3+ distinct steps, or has multiple requirements. Create the tasks up front, then work through them one by one.
        - This keeps you anchored to the user's actual request and prevents drifting into unrelated exploration.
        Skip for trivial single-step work.

        Make each task a concrete, verifiable outcome (e.g. "Add /api/v1/search endpoint", not "look at code").
        After creating tasks, use TaskUpdate to drive them and TaskList to review what's left.
        """;
    public bool IsReadOnly => true; // 세션 인메모리 상태만 변경 (권한 게이트 우회)
    public bool IsConcurrencySafe => true;

    public JsonElement InputSchema { get; } = TaskJson.Parse(
        """{"type":"object","properties":{"subject":{"type":"string"}},"required":["subject"]}""");

    private sealed record Input([property: JsonPropertyName("subject")] string? Subject);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        var inp = input.Deserialize<Input>();
        if (inp is null || string.IsNullOrWhiteSpace(inp.Subject))
        {
            yield return new ToolOutput("TaskCreate: 'subject' is required", IsError: true);
            yield break;
        }

        var item = store.Add(inp.Subject);
        yield return new ToolOutput($"created task #{item.Id}: {item.Subject}");
    }
}

/// <summary>태스크 목록.</summary>
public sealed class TaskListTool(TaskStore store) : ITool
{
    public string Name => "TaskList";

    public string Description => """
        List tasks and their status (pending/in_progress/completed).
        Use this to re-anchor on the plan and pick the next task — especially after a long stretch of
        reading/exploration, or right after context was compacted, to make sure you are still on the user's request.
        """;
    public bool IsReadOnly => true;
    public bool IsConcurrencySafe => true;

    public JsonElement InputSchema { get; } = TaskJson.Parse("""{"type":"object","properties":{}}""");

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        var all = store.All();
        if (all.Count == 0)
        {
            yield return new ToolOutput("(no tasks)");
            yield break;
        }

        var sb = new StringBuilder();
        foreach (var t in all)
        {
            sb.AppendLine($"#{t.Id} [{t.Status}] {t.Subject}");
        }

        yield return new ToolOutput(sb.ToString().TrimEnd());
    }
}

/// <summary>태스크 상태 변경.</summary>
public sealed class TaskUpdateTool(TaskStore store) : ITool
{
    public string Name => "TaskUpdate";

    public string Description => """
        Update a task's status: pending | in_progress | completed.

        Rules (follow exactly):
        - Mark a task in_progress BEFORE you start working on it. Keep EXACTLY ONE task in_progress at a time.
        - Mark it completed IMMEDIATELY after finishing it — do not batch multiple completions for later.
        - Do NOT mark completed if the work failed, tests didn't pass, or you couldn't verify it. Leave it
          in_progress and state the blocker instead. A completed task means it is truly done and verified.
        - When all tasks are completed, the user's request is done — stop, don't invent extra verification work.
        """;
    public bool IsReadOnly => true;
    public bool IsConcurrencySafe => true;

    public JsonElement InputSchema { get; } = TaskJson.Parse(
        """{"type":"object","properties":{"id":{"type":"string"},"status":{"type":"string"}},"required":["id","status"]}""");

    private sealed record Input(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("status")] string? Status);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        var inp = input.Deserialize<Input>();
        if (inp is null || string.IsNullOrWhiteSpace(inp.Id) || string.IsNullOrWhiteSpace(inp.Status))
        {
            yield return new ToolOutput("TaskUpdate: 'id' and 'status' are required", IsError: true);
            yield break;
        }

        var ok = store.Update(inp.Id, TaskJson.ParseStatus(inp.Status));
        yield return ok
            ? new ToolOutput($"updated task #{inp.Id} → {TaskJson.ParseStatus(inp.Status)}")
            : new ToolOutput($"task #{inp.Id} not found", IsError: true);
    }
}
