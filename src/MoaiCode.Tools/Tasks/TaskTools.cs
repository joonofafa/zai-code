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

/// <summary>페이즈 트리 평문 렌더(툴 출력·TUI 공용). 마크업 없는 순수 텍스트.</summary>
public static class PlanRender
{
    public static string PlainTree(IReadOnlyList<PhaseView> phases)
    {
        var sb = new StringBuilder();
        foreach (var ph in phases)
        {
            var mark = ph.Status switch
            {
                PhaseStatus.Done => "[x]",
                PhaseStatus.Active => "[>]",
                _ => "[ ]",
            };
            sb.AppendLine($"{mark} Phase {ph.Number}: {ph.Title}");
            foreach (var t in ph.Tasks)
            {
                var tm = t.Status switch
                {
                    TaskStatus.Completed => "  x",
                    TaskStatus.InProgress => "  >",
                    _ => "  -",
                };
                sb.AppendLine($"{tm} #{t.Id} {t.Subject}");
            }
        }

        return sb.ToString().TrimEnd();
    }
}

/// <summary>목표를 순차 페이즈(Phase)로 분해해 실행 계획을 세운다. 기존 태스크/플랜을 교체한다.</summary>
public sealed class PlanCreateTool(TaskStore store) : ITool
{
    public string Name => "PlanCreate";

    public string Description => """
        Lay out a phased execution plan: decompose the goal into ordered phases, each with concrete tasks.
        Phases run sequentially (Phase 1 → 2 → …); complete every task in a phase before the next begins.
        Calling this REPLACES any existing plan/tasks.

        When to use:
        - Multi-step build/migration work that benefits from staged execution (setup → core → verification, etc.).
        - After investigating in plan mode: emit the plan here, then execute phase by phase.

        Make each task a concrete, verifiable outcome. Keep phases minimal and ordered by dependency.
        Input: { "phases": [ { "title": "...", "tasks": ["...", "..."] }, ... ] }
        """;
    public bool IsReadOnly => true; // 세션 인메모리 상태만 변경 (권한 게이트 우회)
    public bool IsConcurrencySafe => true;

    public JsonElement InputSchema { get; } = TaskJson.Parse(
        """
        {"type":"object","properties":{"phases":{"type":"array","items":{"type":"object",
        "properties":{"title":{"type":"string"},"tasks":{"type":"array","items":{"type":"string"}}},
        "required":["title","tasks"]}}},"required":["phases"]}
        """);

    private sealed record PhaseInput(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("tasks")] List<string>? Tasks);

    private sealed record Input([property: JsonPropertyName("phases")] List<PhaseInput>? Phases);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        var inp = input.Deserialize<Input>();
        if (inp?.Phases is null || inp.Phases.Count == 0)
        {
            yield return new ToolOutput("PlanCreate: 'phases' (non-empty) is required", IsError: true);
            yield break;
        }

        var phases = new List<(string, IReadOnlyList<string>)>();
        foreach (var p in inp.Phases)
        {
            var title = string.IsNullOrWhiteSpace(p.Title) ? $"Phase {phases.Count + 1}" : p.Title!;
            var tasks = (p.Tasks ?? new List<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (tasks.Count == 0)
            {
                yield return new ToolOutput($"PlanCreate: phase '{title}' has no tasks", IsError: true);
                yield break;
            }

            phases.Add((title, tasks));
        }

        store.SetPlan(phases);
        var tree = PlanRender.PlainTree(store.Phases());
        yield return new ToolOutput($"plan created ({phases.Count} phases):\n{tree}");
    }
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

        // 페이즈드 플랜이 있으면 트리로, 아니면 평면 목록으로.
        var phases = store.Phases();
        if (phases.Count > 0)
        {
            var cur = store.CurrentPhase();
            var head = cur is null ? "all phases complete" : $"current: Phase {cur}";
            yield return new ToolOutput($"{head}\n{PlanRender.PlainTree(phases)}");
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
