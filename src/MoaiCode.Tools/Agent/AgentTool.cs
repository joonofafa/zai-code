using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoaiCode.Core.Agent;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;

namespace MoaiCode.Tools.Agent;

/// <summary>
/// 서브에이전트 spawn 툴 (TS AgentTool 대응, 단순화). 별도 QueryEngine을 만들어
/// 하위 프롬프트를 자율 수행하고 최종 텍스트만 반환. 무한 재귀 방지를 위해
/// 서브툴 목록에는 AgentTool 자신을 포함하지 않음 (호출측 책임).
/// </summary>
public sealed class AgentTool : ITool
{
    private readonly IChatModel _model;
    private readonly IReadOnlyList<ITool> _subTools;
    private readonly int _maxTurns;

    public AgentTool(IChatModel model, IReadOnlyList<ITool> subTools, int maxTurns = 8)
    {
        _model = model;
        _subTools = subTools;
        _maxTurns = maxTurns;
    }

    public string Name => "Agent";

    public string Description => """
        Launch a new agent to handle a complex, multi-step task autonomously and return its result.

        Available subagent types:
        - general-purpose (default): multi-step research and task execution.
        - explore: fast, READ-ONLY codebase search and exploration.
        - plan: READ-ONLY architecture and implementation planning.

        Use this when a task would otherwise fill your context with raw output you won't need again,
        or when independent research can run in parallel. If you delegate research to a subagent, do not
        also perform the same searches yourself.
        """;

    public bool IsReadOnly => false;
    public bool IsConcurrencySafe => false;

    public JsonElement InputSchema { get; } = Parse(
        """
        {
          "type": "object",
          "properties": {
            "description": { "type": "string", "description": "Short 3-5 word task label" },
            "prompt": { "type": "string", "description": "The task for the sub-agent to perform" },
            "subagent_type": { "type": "string", "description": "general-purpose | explore | plan (default general-purpose)" }
          },
          "required": ["prompt"]
        }
        """);

    private sealed record Input(
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("prompt")] string? Prompt,
        [property: JsonPropertyName("subagent_type")] string? SubagentType);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        var inp = input.Deserialize<Input>();
        if (inp is null || string.IsNullOrWhiteSpace(inp.Prompt))
        {
            yield return new ToolOutput("Agent: 'prompt' is required", IsError: true);
            yield break;
        }

        // 서브에이전트는 자동 승인 (대화형 권한은 메인 에이전트에서만).
        // extendTurns:false — 서브에이전트는 maxTurns에서 멈춘다(연장 금지). 연장은 메인 에이전트 전용으로,
        // 서브에이전트까지 연장하면 한 번 spawn에 수십 턴×원격모델 지연으로 몇 분씩 걸린다.
        var subEngine = new QueryEngine(
            _model,
            _subTools,
            new AutoApproveGate(),
            maxTurns: _maxTurns,
            workingDirectory: context.WorkingDirectory,
            extendTurns: false);
        subEngine.Seed(new[]
        {
            new SystemMessage(SubAgentPrompts.ForType(inp.SubagentType)),
        });

        var sb = new StringBuilder();
        await foreach (var ev in subEngine.SubmitAsync(inp.Prompt, ct).WithCancellation(ct))
        {
            if (ev is TextDelta d)
            {
                sb.Append(d.Text);
            }
        }

        yield return new ToolOutput(sb.Length == 0 ? "(sub-agent produced no output)" : sb.ToString());
    }

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
