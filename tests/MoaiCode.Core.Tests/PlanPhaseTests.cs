using System.Runtime.CompilerServices;
using System.Text.Json;
using MoaiCode.Core.Agent;
using MoaiCode.Core.Agent.Prompts;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;
using MoaiCode.Tools.Tasks;
using Xunit;
using TaskStatus = MoaiCode.Tools.Tasks.TaskStatus;

namespace MoaiCode.Core.Tests;

public class TaskStorePhaseTests
{
    [Fact]
    public void SetPlan_groups_into_ordered_phases_with_status()
    {
        var s = new TaskStore();
        s.SetPlan(new (string, IReadOnlyList<string>)[]
        {
            ("Setup", new[] { "a", "b" }),
            ("Core", new[] { "c" }),
        });

        var phases = s.Phases();
        Assert.Equal(2, phases.Count);
        Assert.Equal(1, phases[0].Number);
        Assert.Equal("Setup", phases[0].Title);
        Assert.Equal(2, phases[0].Tasks.Count);
        // 초기: 1번 Active, 2번 Pending, 현재 페이즈=1
        Assert.Equal(PhaseStatus.Active, phases[0].Status);
        Assert.Equal(PhaseStatus.Pending, phases[1].Status);
        Assert.Equal(1, s.CurrentPhase());
    }

    [Fact]
    public void CurrentPhase_advances_only_when_phase_fully_complete()
    {
        var s = new TaskStore();
        s.SetPlan(new (string, IReadOnlyList<string>)[]
        {
            ("P1", new[] { "a", "b" }),
            ("P2", new[] { "c" }),
        });
        var ids = s.All();

        // 1번 페이즈의 태스크 하나만 완료 → 여전히 1번
        s.Update(ids[0].Id, TaskStatus.Completed);
        Assert.Equal(1, s.CurrentPhase());

        // 1번 전부 완료 → 2번으로 전진
        s.Update(ids[1].Id, TaskStatus.Completed);
        Assert.Equal(2, s.CurrentPhase());
        var ph = s.Phases();
        Assert.Equal(PhaseStatus.Done, ph[0].Status);
        Assert.Equal(PhaseStatus.Active, ph[1].Status);

        // 전부 완료 → null
        s.Update(ids[2].Id, TaskStatus.Completed);
        Assert.Null(s.CurrentPhase());
    }
}

public class PlanCreateToolTests
{
    [Fact]
    public async Task PlanCreate_populates_store_as_phased_tasks()
    {
        var store = new TaskStore();
        var tool = new PlanCreateTool(store);
        var input = JsonDocument.Parse(
            """{"phases":[{"title":"Setup","tasks":["init repo","add deps"]},{"title":"Build","tasks":["impl"]}]}""")
            .RootElement;
        var ctx = new ToolContext(Directory.GetCurrentDirectory(), PermissionMode.Auto, new ReadTracker());

        string? last = null;
        await foreach (var p in tool.ExecuteAsync(input, ctx, default))
        {
            if (p is ToolOutput o)
            {
                last = o.Text;
                Assert.False(o.IsError);
            }
        }

        Assert.Equal(2, store.Phases().Count);
        Assert.Equal(3, store.All().Count);
        Assert.Equal(1, store.CurrentPhase());
        Assert.Contains("Phase 1: Setup", last);
    }
}

public class QueryEnginePhaseBoundaryTests
{
    // 1턴에 툴 1회 호출(→ 그 시점에 페이즈가 1→2로 전진), 2턴엔 툴 없이 종료.
    private sealed class ToolThenDoneModel : IChatModel
    {
        private int _n;

        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            IReadOnlyList<Message> messages, IReadOnlyList<ITool> tools,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            if (_n++ == 0)
            {
                using var d = JsonDocument.Parse("{}");
                yield return new ToolCallRequested(new ToolUseBlock("c1", "Advance", d.RootElement.Clone()));
                yield return new TurnCompleted(new Usage(1, 1), "tool_calls");
                yield break;
            }

            yield return new TextDelta("done");
            yield return new TurnCompleted(new Usage(1, 1), "stop");
        }
    }

    // 실행되면 현재 페이즈를 1→2로 올리는 테스트용 툴(태스크 완료로 페이즈가 넘어가는 상황 시뮬레이션).
    private sealed class AdvanceTool(int[] phaseHolder) : ITool
    {
        public string Name => "Advance";
        public string Description => "advance phase";
        public bool IsReadOnly => true;
        public bool IsConcurrencySafe => true;
        public JsonElement InputSchema { get; } = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

        public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
            JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            phaseHolder[0] = 2;
            yield return new ToolOutput("advanced");
        }
    }

    [Fact]
    public async Task Crossing_phase_boundary_emits_notice_and_reanchor_reminder()
    {
        var phase = new[] { 1 };   // 현재 페이즈 홀더(초기 1)
        var engine = new QueryEngine(
            new ToolThenDoneModel(),
            new ITool[] { new AdvanceTool(phase) },
            currentPhase: () => phase[0]);
        engine.Seed(new[] { new SystemMessage("sys") });

        var sawNotice = false;
        await foreach (var ev in engine.SubmitAsync("go"))
        {
            if (ev is StreamNotice sn && sn.Text.Contains("Starting phase 2"))
            {
                sawNotice = true;
            }
        }

        Assert.True(sawNotice, "phase-boundary StreamNotice should be emitted when phase advances 1→2");
        // 다음 페이즈 재고정 리마인더가 대화에 주입됐다.
        var reminders = engine.Messages.OfType<UserMessage>().Select(m => m.Text);
        Assert.Contains(reminders, t => t == Reminders.PhaseAdvanced);
    }
}
