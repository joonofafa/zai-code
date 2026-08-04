using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoaiCode.Core.Memory;
using MoaiCode.Core.Tools;

namespace MoaiCode.Tools.Memory;

/// <summary>
/// 프로젝트 자동 메모리(세션 간 유지). 배운 사실을 저장/갱신/삭제하고, 인덱스(MEMORY.md)는
/// 매 세션 시작 시 시스템 프롬프트에 주입된다. 저장소는 ~/.moai/projects/&lt;slug&gt;/memory/ (커밋 안 됨).
/// </summary>
public sealed class MemoryTool : ITool
{
    public string Name => "Memory";

    public string Description => """
        Persistent, project-scoped memory that survives across sessions.

        Use this to remember durable facts you learn while working — deployment/build/access procedures, project constraints and decisions, and user preferences — so a future session does not have to rediscover them. The memory index (MEMORY.md) is loaded into your context at the start of every session.

        action:
        - "save": create or update a memory. Requires 'name' (short kebab-case id) and 'content'. Optional 'description' (one line, shown in the index) and 'type' (user | feedback | project | reference). Saving the same name overwrites it.
        - "delete": remove a memory by 'name'.
        - "list": list current memories.

        SAVE: procedures/commands that were non-obvious to derive, constraints not visible in the code, decisions and their rationale, user preferences and corrections.
        Do NOT save: things the repository or git history already record (code structure, existing CLAUDE.md), or details that only matter to the current conversation.
        """;

    public bool IsReadOnly => false;
    public bool IsConcurrencySafe => false;

    public JsonElement InputSchema { get; } = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["save", "delete", "list"] },
            "name": { "type": "string", "description": "Short kebab-case id (also the filename). Required for save/delete." },
            "description": { "type": "string", "description": "One-line summary shown in the index. Used for save." },
            "type": { "type": "string", "enum": ["user", "feedback", "project", "reference"] },
            "content": { "type": "string", "description": "The fact to remember. Required for save." }
          },
          "required": ["action"]
        }
        """);

    private sealed record Input(
        [property: JsonPropertyName("action")] string? Action,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("content")] string? Content);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask.ConfigureAwait(false);

        var inp = input.Deserialize<Input>();
        var action = (inp?.Action ?? string.Empty).Trim().ToLowerInvariant();
        var cwd = context.WorkingDirectory;

        switch (action)
        {
            case "save":
                if (inp is null || string.IsNullOrWhiteSpace(inp.Name) || string.IsNullOrWhiteSpace(inp.Content))
                {
                    yield return new ToolOutput("Memory save: 'name' and 'content' are required.", IsError: true);
                    yield break;
                }

                yield return new ToolOutput(ProjectMemory.Save(cwd, inp.Name!, inp.Description, inp.Type, inp.Content!));
                break;

            case "delete":
                if (inp is null || string.IsNullOrWhiteSpace(inp.Name))
                {
                    yield return new ToolOutput("Memory delete: 'name' is required.", IsError: true);
                    yield break;
                }

                yield return new ToolOutput(ProjectMemory.Delete(cwd, inp.Name!));
                break;

            case "list":
                yield return new ToolOutput(ProjectMemory.List(cwd));
                break;

            default:
                yield return new ToolOutput("Memory: 'action' must be one of save, delete, list.", IsError: true);
                break;
        }
    }
}
