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
    private readonly IPermissionGate? _gate;

    // gate: 하위 에이전트가 쓸 권한 게이트(부모와 동일). null 이면 거부(fail-closed) — 보안상
    // 호출측이 반드시 부모 게이트를 넘겨야 한다(SEC-004). 과거 AutoApprove 폴백은 fail-open 이라 제거:
    // 게이트 누락 버그가 서브에이전트의 모든 쓰기 툴을 무승인 실행으로 이어졌다.
    public AgentTool(IChatModel model, IReadOnlyList<ITool> subTools, int maxTurns = 8, IPermissionGate? gate = null)
    {
        _model = model;
        _subTools = subTools;
        _maxTurns = maxTurns;
        _gate = gate;
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

        // 서브에이전트도 부모와 동일한 권한 게이트를 쓴다(SEC-004): 워크스페이스 경계·deny 규칙·파괴적
        // 명령 차단·위험 분류가 하위 도구 호출에도 적용된다. 게이트가 주입되지 않았다면 거부(fail-closed).
        // extendTurns:false — 서브에이전트는 maxTurns에서 멈춘다(연장 금지). 연장은 메인 에이전트 전용으로,
        // 서브에이전트까지 연장하면 한 번 spawn에 수십 턴×원격모델 지연으로 몇 분씩 걸린다.
        var subEngine = new QueryEngine(
            _model,
            _subTools,
            _gate ?? new DenyAllGate(),
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

        // 서브에이전트 원문에도 추론 마커가 섞여 있다 — 부모 컨텍스트를 오염시키지 않도록 제거.
        var output = ThinkFilter.Strip(sb.ToString());
        yield return new ToolOutput(output.Length == 0 ? "(sub-agent produced no output)" : output);
    }

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
