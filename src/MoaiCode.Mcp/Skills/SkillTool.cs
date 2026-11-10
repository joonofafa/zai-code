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
    private readonly Dictionary<string, Skill> _skills = new(StringComparer.OrdinalIgnoreCase);

    public SkillTool(IReadOnlyList<Skill> skills)
    {
        Reload(skills);
        InputSchema = Parse(
            """
            {
              "type": "object",
              "properties": { "name": { "type": "string", "description": "Skill name to invoke" } },
              "required": ["name"]
            }
            """);
    }

    /// <summary>스킬 목록을 교체한다(예: /skills sync 로 팀 공유 스킬을 라이브 갱신). Description 도 갱신.</summary>
    public void Reload(IReadOnlyList<Skill> skills)
    {
        _skills.Clear();
        foreach (var s in skills)
        {
            _skills[s.Name] = s;
        }

        var names = string.Join(", ", skills.Select(s => $"{s.Name} — {s.Description}"));
        Description = skills.Count == 0
            ? "Invoke a skill by name. (no skills installed)"
            : $"Invoke a skill by name. Available: {names}";
    }

    public string Name => "Skill";
    public string Description { get; private set; } = string.Empty;
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

        yield return new ToolOutput(BodyWithBaseDirectory(skill));
    }

    /// <summary>
    /// 폴더형 스킬({name}/SKILL.md)은 본문이 references/·scripts/ 같은 옆 파일을 상대 경로로 가리킨다 —
    /// 모델이 그 파일을 찾을 수 있게 스킬 폴더의 절대 경로를 앞에 붙인다(Claude Code 와 같은 방식).
    /// 단일 파일 스킬({name}.md)은 옆 파일이 없으므로 본문 그대로.
    /// </summary>
    internal static string BodyWithBaseDirectory(Skill skill)
    {
        if (!string.Equals(Path.GetFileName(skill.Path), "SKILL.md", StringComparison.OrdinalIgnoreCase))
        {
            return skill.Body;
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(skill.Path));
        return $"Base directory for this skill: {dir}\n" +
               "(Relative paths in this skill, such as references/ or scripts/, are inside that directory.)\n\n" +
               skill.Body;
    }

    /// <summary>
    /// 켜진 상시(passive) 스킬 본문을 시스템 프롬프트 절로 묶는다. 없으면 null(절 생략).
    /// 메인 에이전트와 하위 에이전트 프롬프트가 같은 문구를 쓴다.
    /// </summary>
    public static string? AlwaysOnSection(IEnumerable<Skill> passive)
    {
        var list = passive.ToList();
        if (list.Count == 0)
        {
            return null;
        }

        var parts = list.Select(s => $"## {s.Name}\n\n{BodyWithBaseDirectory(s).TrimEnd()}");
        return "# Always-on skills\n\n" +
               "The user turned these skills on for every task. Follow them whenever the work they describe comes up, " +
               "without being asked and without invoking the Skill tool for them.\n\n" +
               string.Join("\n\n", parts);
    }

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
