using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoaiCode.Core.Tools;

namespace MoaiCode.Mcp.Skills;

/// <summary>
/// 스킬 호출 툴 (Claude Code의 Skill 툴 대응). 모델이 {name}으로 호출하면
/// 해당 스킬의 본문(지침)을 반환하고, 모델은 그 지침을 따라 작업을 수행.
/// </summary>
public sealed class SkillTool : ITool
{
    private readonly Dictionary<string, Skill> _skills;

    public SkillTool(IReadOnlyList<Skill> skills)
    {
        _skills = skills.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var names = string.Join(", ", skills.Select(s => $"{s.Name} — {s.Description}"));
        Description = skills.Count == 0
            ? "Invoke a skill by name. (no skills installed)"
            : $"Invoke a skill by name. Available: {names}";
        InputSchema = Parse(
            """
            {
              "type": "object",
              "properties": { "name": { "type": "string", "description": "Skill name to invoke" } },
              "required": ["name"]
            }
            """);
    }

    public string Name => "Skill";
    public string Description { get; }
    public JsonElement InputSchema { get; }
    public bool IsReadOnly => true;
    public bool IsConcurrencySafe => true;

    private sealed record Input([property: JsonPropertyName("name")] string? Name);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;

        var inp = input.Deserialize<Input>();
        if (inp is null || string.IsNullOrWhiteSpace(inp.Name))
        {
            yield return new ToolOutput("Skill: 'name' is required", IsError: true);
            yield break;
        }

        if (!_skills.TryGetValue(inp.Name, out var skill))
        {
            var available = string.Join(", ", _skills.Keys);
            yield return new ToolOutput($"Unknown skill '{inp.Name}'. Available: {available}", IsError: true);
            yield break;
        }

        yield return new ToolOutput(skill.Body);
    }

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
