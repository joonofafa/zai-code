using System.Runtime.CompilerServices;
using System.Text.Json;
using MoaiCode.Core.Agent;
using MoaiCode.Core.Agent.Prompts;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;
using MoaiCode.Tools.Agent;
using MoaiCode.Tools.Files;
using Xunit;

namespace MoaiCode.Core.Tests;

public class SystemPromptBuilderTests
{
    [Fact]
    public void Builds_expected_sections()
    {
        var p = SystemPromptBuilder.Build(new PromptContext
        {
            WorkingDirectory = "/proj",
            IsGitRepo = true,
            Platform = "linux",
            OsVersion = "Linux 6.17",
            CurrentDate = "2026-06-26",
            ModelDescription = "You are powered by the model kimi.",
            ToolNames = new[] { "Read", "Bash", "Glob" },
            ClaudeMd = "# Project\nUse tabs.",
        });

        Assert.Contains("You are MoAI Code", p);
        Assert.Contains("authorized security testing", p);   // cyber risk
        Assert.Contains("file_path:line_number", p);          // tone & style
        Assert.Contains("Primary working directory: /proj", p);
        Assert.Contains("To read files use Read", p);          // using-tools (Read present)
        Assert.Contains("Use tabs.", p);                       // CLAUDE.md injection
        Assert.Contains("# Coding guidelines", p);             // 전역 기본 행동 지침
        Assert.Contains("Simplicity First", p);
        Assert.Contains("Every changed line should trace directly", p);
        Assert.DoesNotContain("# Working with documents", p);  // 문서 툴 없으면 문서 섹션 제외
    }

    [Fact]
    public void Includes_document_section_only_when_doc_tools_present()
    {
        var baseCtx = new PromptContext
        {
            WorkingDirectory = "/proj",
            Platform = "windows",
            ToolNames = new[] { "Read", "DocxCreate", "XlsxCreate", "PptxCreate" },
        };

        var withDocs = SystemPromptBuilder.Build(baseCtx);
        Assert.Contains("# Working with documents", withDocs);
        Assert.Contains("polished deliverable", withDocs);
        Assert.Contains("not for document body text", withDocs); // 간결성 예외 명시

        var codingOnly = SystemPromptBuilder.Build(new PromptContext
        {
            WorkingDirectory = "/proj",
            ToolNames = new[] { "Read", "Edit", "Bash" },
        });
        Assert.DoesNotContain("# Working with documents", codingOnly);
    }
}

public class FileReadReminderTests : IDisposable
{
    private readonly string _dir;

    public FileReadReminderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "occs-rmd-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private async Task<string> Read(string file)
    {
        File.WriteAllText(Path.Combine(_dir, "f.txt"), file);
        using var doc = JsonDocument.Parse("""{"path":"f.txt"}""");
        var sb = "";
        await foreach (var p in new FileReadTool().ExecuteAsync(
            doc.RootElement, new ToolContext(_dir, PermissionMode.Auto), default))
        {
            if (p is ToolOutput o)
            {
                sb += o.Text;
            }
        }

        return sb;
    }

    [Fact]
    public async Task Empty_file_returns_reminder()
        => Assert.Contains("contents are empty", await Read(""));

    [Fact]
    public async Task Read_appends_malware_reminder()
    {
        var output = await Read("hello world\n");
        Assert.Contains("hello world", output);
        Assert.Contains("malware", output);
    }
}

public class ToolFailureGuardTests
{
    private sealed class AlwaysToolModel : IChatModel
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            IReadOnlyList<Message> messages, IReadOnlyList<ITool> tools,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            using var d = JsonDocument.Parse("{}");
            yield return new ToolCallRequested(new ToolUseBlock("c", "Fail", d.RootElement.Clone()));
            yield return new TurnCompleted(new Usage(1, 1), "tool_calls");
        }
    }

    private sealed class FailingTool : ITool
    {
        public string Name => "Fail";
        public string Description => "always fails";
        public bool IsReadOnly => true; // bypass permission gate
        public bool IsConcurrencySafe => true;
        public JsonElement InputSchema { get; } = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

        public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
            JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return new ToolOutput("boom", IsError: true);
        }
    }

    [Fact]
    public async Task Stops_after_three_repeated_failures()
    {
        var engine = new QueryEngine(new AlwaysToolModel(), new ITool[] { new FailingTool() });
        engine.Seed(new[] { new SystemMessage("sys") });

        var executions = 0;
        var loopStopped = false;
        await foreach (var ev in engine.SubmitAsync("go"))
        {
            switch (ev)
            {
                case ToolExecuted:
                    executions++;
                    break;
                case TurnCompleted c when c.StopReason == "tool_failure_loop":
                    loopStopped = true;
                    break;
            }
        }

        Assert.True(loopStopped);
        Assert.Equal(3, executions);
    }

    // 매 호출 인자가 다른 모델 — 서로 다른 실패는 '루프'가 아니므로 가드에 안 걸려야 한다.
    private sealed class VaryingArgsToolModel : IChatModel
    {
        private int _n;

        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            IReadOnlyList<Message> messages, IReadOnlyList<ITool> tools,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            _n++;
            using var d = JsonDocument.Parse($"{{\"path\":\"p{_n}\"}}");
            yield return new ToolCallRequested(new ToolUseBlock("c" + _n, "Fail", d.RootElement.Clone()));
            yield return new TurnCompleted(new Usage(1, 1), "tool_calls");
        }
    }

    [Fact]
    public async Task Distinct_failing_calls_do_not_trip_loop_guard()
    {
        var engine = new QueryEngine(
            new VaryingArgsToolModel(), new ITool[] { new FailingTool() }, maxTurns: 5);
        engine.Seed(new[] { new SystemMessage("sys") });

        var loopStopped = false;
        var hitMaxTurns = false;
        var executions = 0;
        await foreach (var ev in engine.SubmitAsync("go"))
        {
            switch (ev)
            {
                case ToolExecuted:
                    executions++;
                    break;
                case TurnCompleted c when c.StopReason == "tool_failure_loop":
                    loopStopped = true;
                    break;
                case TurnCompleted c when c.StopReason == "max_turns":
                    hitMaxTurns = true;
                    break;
            }
        }

        Assert.False(loopStopped);      // 서로 다른 호출은 루프로 오인하지 않음
        Assert.True(hitMaxTurns);       // 연장(압축 후 계속)을 모두 쓴 뒤에야 max_turns
        Assert.True(executions > 5);    // maxTurns(5)를 넘겨 연장이 실제로 일어남
    }

    [Fact]
    public async Task No_extension_stops_exactly_at_max_turns()
    {
        // extendTurns:false (서브에이전트) — 연장 없이 maxTurns에서 정확히 멈춘다.
        var engine = new QueryEngine(
            new VaryingArgsToolModel(), new ITool[] { new FailingTool() }, maxTurns: 3, extendTurns: false);
        engine.Seed(new[] { new SystemMessage("sys") });

        var executions = 0;
        var hitMaxTurns = false;
        await foreach (var ev in engine.SubmitAsync("go"))
        {
            if (ev is ToolExecuted)
            {
                executions++;
            }
            else if (ev is TurnCompleted c && c.StopReason == "max_turns")
            {
                hitMaxTurns = true;
            }
        }

        Assert.True(hitMaxTurns);
        Assert.Equal(3, executions);    // 연장 없이 정확히 maxTurns
    }
}

public class ThinkFilterTests
{
    [Fact]
    public void Strips_matched_think_pair()
        => Assert.Equal("answer", ThinkFilter.Strip("<think>reasoning</think>answer"));

    [Fact]
    public void Strips_orphan_close_keeps_answer()
        => Assert.Equal("the answer", ThinkFilter.Strip("some reasoning text </think> the answer"));

    [Fact]
    public void Strips_orphan_open_tail()
        => Assert.Equal("answer", ThinkFilter.Strip("answer <think>dangling reasoning to end"));

    [Fact]
    public void Passthrough_when_no_think()
        => Assert.Equal("plain coding answer", ThinkFilter.Strip("plain coding answer"));

    [Fact]
    public void Strips_concatenated_thinking_status_markers()
        => Assert.Equal("", ThinkFilter.Strip(
            "THINKING_STATUS:Analyzing and reasoning deeply...THINKING_STATUS:Reasoning... (0s)"));

    [Fact]
    public void Strips_thinking_status_but_keeps_real_answer_on_next_line()
        => Assert.Equal("Here is the review.", ThinkFilter.Strip(
            "THINKING_STATUS:Reasoning...\nHere is the review."));

    // raw 실제 형식: __THINKING_STATUS__: (밑줄 2개). 노이즈는 버리고 마커 뒤 답변은 보존.
    [Fact]
    public void Strips_underscored_status_markers_keeps_answer()
        => Assert.Equal("좋습니다.", ThinkFilter.Strip(
            "__THINKING_STATUS__:Analyzing and reasoning deeply..." +
            "__THINKING_STATUS__:Reasoning... (30s)__THINKING_STATUS__:좋습니다."));

    [Fact]
    public void Strips_underscored_marker_preserves_multiline_markdown()
        => Assert.Equal("Answer:\n\n## Title\n\n**bold** text", ThinkFilter.Strip(
            "__THINKING_STATUS__:Reasoning... (1s)__THINKING_STATUS__:Answer:\n\n## Title\n\n**bold** text"));
}

public class OutputRecoveryTests
{
    // 첫 응답은 출력 한도로 잘리고(stop=length), 재개 시 마저 출력(stop=stop).
    private sealed class TruncatedThenDoneModel : IChatModel
    {
        private int _calls;

        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            IReadOnlyList<Message> messages, IReadOnlyList<ITool> tools,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            _calls++;
            if (_calls == 1)
            {
                yield return new TextDelta("part1");
                yield return new TurnCompleted(new Usage(1, 1), "length");
            }
            else
            {
                yield return new TextDelta("part2");
                yield return new TurnCompleted(new Usage(1, 1), "stop");
            }
        }
    }

    // 1차: stop=tool_calls 인데 실제 툴콜 0개(누락). 2차: 정상 응답.
    private sealed class EmptyToolCallThenDoneModel : IChatModel
    {
        private int _calls;

        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            IReadOnlyList<Message> messages, IReadOnlyList<ITool> tools,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            _calls++;
            if (_calls == 1)
            {
                yield return new TextDelta("I'll restart it");
                yield return new TurnCompleted(new Usage(1, 1), "tool_calls");
            }
            else
            {
                yield return new TextDelta("done");
                yield return new TurnCompleted(new Usage(1, 1), "stop");
            }
        }
    }

    [Fact]
    public async Task Empty_tool_calls_turn_is_retried()
    {
        var engine = new QueryEngine(new EmptyToolCallThenDoneModel(), Array.Empty<ITool>());
        engine.Seed(new[] { new SystemMessage("sys") });

        var text = "";
        await foreach (var ev in engine.SubmitAsync("go"))
        {
            if (ev is TextDelta d)
            {
                text += d.Text;
            }
        }

        Assert.Contains("done", text); // 빈 tool_calls 후 재요청으로 계속 진행
        Assert.Contains(engine.Messages, m =>
            m is UserMessage u && u.Text.Contains("no tool call was received"));
    }

    // 1차: 액션 예고만("~하겠습니다") 하고 stop. 2차: 실제 완료.
    private sealed class AnnouncesThenDoneModel : IChatModel
    {
        private int _calls;

        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            IReadOnlyList<Message> messages, IReadOnlyList<ITool> tools,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            _calls++;
            if (_calls == 1)
            {
                yield return new TextDelta("서버를 재시작하겠습니다");
                yield return new TurnCompleted(new Usage(1, 1), "stop");
            }
            else
            {
                yield return new TextDelta("재시작 완료");
                yield return new TurnCompleted(new Usage(1, 1), "stop");
            }
        }
    }

    [Fact]
    public async Task Announced_action_without_tool_is_nudged_once()
    {
        var engine = new QueryEngine(new AnnouncesThenDoneModel(), Array.Empty<ITool>());
        engine.Seed(new[] { new SystemMessage("sys") });

        var text = "";
        await foreach (var ev in engine.SubmitAsync("계속 진행해"))
        {
            if (ev is TextDelta d)
            {
                text += d.Text;
            }
        }

        Assert.Contains("완료", text); // 예고만 하고 멈추지 않고 이어서 완료까지
    }

    [Fact]
    public async Task Truncated_output_is_resumed()
    {
        var engine = new QueryEngine(new TruncatedThenDoneModel(), Array.Empty<ITool>());
        engine.Seed(new[] { new SystemMessage("sys") });

        var text = "";
        await foreach (var ev in engine.SubmitAsync("go"))
        {
            if (ev is TextDelta d)
            {
                text += d.Text;
            }
        }

        Assert.Contains("part1", text);   // 잘린 1차
        Assert.Contains("part2", text);   // 재개로 이어진 2차
        Assert.Contains(engine.Messages, m =>
            m is UserMessage u && u.Text.Contains("cut off"));  // 복구 리마인더 주입
    }
}

public class SubAgentPromptTests
{
    [Fact]
    public void Maps_subagent_types()
    {
        Assert.Contains("READ-ONLY", SubAgentPrompts.ForType("explore"));
        Assert.Contains("planning specialist", SubAgentPrompts.ForType("plan"));
        Assert.Contains("strengths", SubAgentPrompts.ForType(null));
        Assert.Contains("strengths", SubAgentPrompts.ForType("unknown"));
    }
}

public class CompactionTests
{
    private sealed class OkModel : IChatModel
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            IReadOnlyList<Message> messages, IReadOnlyList<ITool> tools,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new TextDelta("ok");
            yield return new TurnCompleted(new Usage(1, 1), "stop");
        }
    }

    [Fact]
    public async Task Long_history_is_compacted_with_safe_boundary()
    {
        // contextWindowTokens=2000 → 임계선 1400 tok(≈5600자). 아래 히스토리는 그걸 훌쩍 넘긴다.
        var engine = new QueryEngine(
            new OkModel(), Array.Empty<ITool>(), contextWindowTokens: 2000);
        engine.Seed(new[] { new SystemMessage("sys") });

        var big = new string('x', 1000); // 메시지당 ~1000자 → 토큰 기반 트리거 확보
        var hist = new List<Message> { new SystemMessage("sys") };
        for (var i = 1; i <= 15; i++)
        {
            hist.Add(new UserMessage($"u{i} {big}"));
            hist.Add(new AssistantMessage(new List<ContentBlock> { new TextBlock($"a{i}") }));
        }

        engine.Restore(hist); // 1 + 30 = 31 messages

        await foreach (var _ in engine.SubmitAsync("go"))
        {
        }

        Assert.True(engine.Messages.Count < 31);
        Assert.Contains(engine.Messages, m =>
            m is UserMessage u && u.Text.StartsWith("[Summary of earlier conversation]"));
    }

    // 요약 호출엔 <analysis>+<summary> 구조로 응답, 메인 호출엔 "ok".
    private sealed class StructuredSummaryModel : IChatModel
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            IReadOnlyList<Message> messages, IReadOnlyList<ITool> tools,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            if (messages.Count == 2 && messages[0] is SystemMessage s && s.Text == CompactionPrompts.BaseCompact)
            {
                yield return new TextDelta("<analysis>SCRATCH_NOTES</analysis>\n<summary>KEPT_SUMMARY</summary>");
                yield return new TurnCompleted(new Usage(1, 1), "stop");
                yield break;
            }

            yield return new TextDelta("ok");
            yield return new TurnCompleted(new Usage(1, 1), "stop");
        }
    }

    [Fact]
    public async Task Compaction_keeps_only_summary_block()
    {
        var engine = new QueryEngine(
            new StructuredSummaryModel(), Array.Empty<ITool>(), contextWindowTokens: 2000);
        engine.Seed(new[] { new SystemMessage("sys") });

        var big = new string('x', 1000);
        var hist = new List<Message> { new SystemMessage("sys") };
        for (var i = 1; i <= 15; i++)
        {
            hist.Add(new UserMessage($"u{i} {big}"));
            hist.Add(new AssistantMessage(new List<ContentBlock> { new TextBlock($"a{i}") }));
        }

        engine.Restore(hist);
        await foreach (var _ in engine.SubmitAsync("go"))
        {
        }

        var summary = engine.Messages
            .OfType<UserMessage>()
            .FirstOrDefault(u => u.Text.StartsWith("[Summary of earlier conversation]"));
        Assert.NotNull(summary);
        Assert.Contains("KEPT_SUMMARY", summary!.Text);
        Assert.DoesNotContain("SCRATCH_NOTES", summary.Text);
        Assert.DoesNotContain("<analysis>", summary.Text);
    }

    private sealed class FakeOverflow : Exception, IModelException
    {
        public bool IsContextOverflow => true;
        public bool IsTransient => false;
    }

    // 첫 '메인' 호출에서 컨텍스트 초과를 던지고, 요약 호출엔 응답하고, 재시도 메인 호출엔 정상 응답.
    private sealed class OverflowThenOkModel : IChatModel
    {
        private bool _threwMain;

        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            IReadOnlyList<Message> messages, IReadOnlyList<ITool> tools,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();

            var isSummarize = messages.Count == 2
                && messages[0] is SystemMessage s
                && s.Text == CompactionPrompts.ContextCollapse;
            if (isSummarize)
            {
                yield return new TextDelta("(summary)");
                yield return new TurnCompleted(new Usage(1, 1), "stop");
                yield break;
            }

            if (!_threwMain)
            {
                _threwMain = true;
                throw new FakeOverflow();
            }

            yield return new TextDelta("ok");
            yield return new TurnCompleted(new Usage(1, 1), "stop");
        }
    }

    [Fact]
    public async Task Context_overflow_triggers_reactive_compaction_and_retries()
    {
        // 창을 크게 잡아 선제 컴팩션은 안 돌게 함 → 컴팩션은 오직 반응형 복구 경로로만 발생.
        var engine = new QueryEngine(
            new OverflowThenOkModel(), Array.Empty<ITool>(), contextWindowTokens: 1_000_000);
        engine.Seed(new[] { new SystemMessage("sys") });

        var hist = new List<Message> { new SystemMessage("sys") };
        for (var i = 1; i <= 8; i++)
        {
            hist.Add(new UserMessage($"u{i}"));
            hist.Add(new AssistantMessage(new List<ContentBlock> { new TextBlock($"a{i}") }));
        }

        engine.Restore(hist);

        var text = "";
        await foreach (var ev in engine.SubmitAsync("go"))
        {
            if (ev is TextDelta d)
            {
                text += d.Text;
            }
        }

        Assert.Contains("ok", text); // 컨텍스트 초과 후 컴팩션→재시도로 정상 응답 도달
        Assert.Contains(engine.Messages, m =>
            m is UserMessage u && u.Text.StartsWith("[Summary of earlier conversation]"));
    }
}
