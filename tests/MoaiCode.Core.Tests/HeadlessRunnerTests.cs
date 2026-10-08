using System.Runtime.CompilerServices;
using MoaiCode.Cli;
using MoaiCode.Core.Agent;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;
using Xunit;

namespace MoaiCode.Core.Tests;

/// <summary>
/// moai run(비대화형)의 stdout 회귀 테스트. 예전엔 TextDelta 를 그대로 흘려보내
/// 추론 마커(__THINKING_STATUS__:…)가 답변과 한 줄로 붙어 파이프 출력을 오염시켰다.
/// </summary>
// Console.SetOut 스왑이 LineEditorWrapTests 와 경합해 간헐 실패(8회 중 1~2회)했으니 직렬화.
[Collection("EnvMutating")]
public sealed class HeadlessRunnerTests
{
    /// <summary>마커가 델타 경계에 걸쳐 쪼개져 오는 실제 스트리밍 형태를 재현.</summary>
    private sealed class SplitMarkerModel : IChatModel
    {
        private readonly string[] _chunks;

        public SplitMarkerModel(params string[] chunks) => _chunks = chunks;

        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            IReadOnlyList<Message> messages,
            IReadOnlyList<ITool> tools,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            foreach (var c in _chunks)
            {
                yield return new TextDelta(c);
            }

            yield return new TurnCompleted(new Usage(1, 1), "stop");
        }
    }

    private static async Task<string> CaptureStdoutAsync(IChatModel model)
    {
        var original = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var engine = new QueryEngine(model, Array.Empty<ITool>(), workingDirectory: Path.GetTempPath());
            var code = await HeadlessRunner.RunAsync(engine, "hi", CancellationToken.None);
            Assert.Equal(0, code);
        }
        finally
        {
            Console.SetOut(original);
        }

        return sw.ToString();
    }

    /// <summary>첫 턴에 없는 툴을 부르고(실패), 다음 턴에 정상 답으로 끝나는 모델.</summary>
    private sealed class FailingToolThenAnswerModel : IChatModel
    {
        private int _calls;

        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            IReadOnlyList<Message> messages,
            IReadOnlyList<ITool> tools,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            if (_calls++ == 0)
            {
                using var doc = System.Text.Json.JsonDocument.Parse("{}");
                yield return new ToolCallRequested(new ToolUseBlock("t1", "NoSuchTool", doc.RootElement.Clone()));
                yield return new TurnCompleted(new Usage(1, 1), "tool_calls");
                yield break;
            }

            yield return new TextDelta("done");
            yield return new TurnCompleted(new Usage(1, 1), "stop");
        }
    }

    /// <summary>본문 없이 출력 한도(stop=length)로만 끝나는 모델 — 추론만 하다 잘린 응답.</summary>
    private sealed class LengthCutOnlyModel : IChatModel
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            IReadOnlyList<Message> messages,
            IReadOnlyList<ITool> tools,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new TurnCompleted(new Usage(1, 1), "length");
        }
    }

    // 답 없이 출력 한도로 끝난 실행은 실패(종료 코드 3)로 알린다 — 예전엔 0 + 빈 출력.
    [Fact]
    public async Task Length_cut_without_answer_fails_the_run()
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        Console.SetOut(new StringWriter());
        Console.SetError(new StringWriter());
        try
        {
            var engine = new QueryEngine(new LengthCutOnlyModel(), Array.Empty<ITool>(), workingDirectory: Path.GetTempPath());
            Assert.Equal(3, await HeadlessRunner.RunAsync(engine, "hi", CancellationToken.None));
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    // 도중의 툴 실패는 실행 실패가 아니다 — 끝까지 마친 실행은 종료 코드 0.
    [Fact]
    public async Task Tool_error_mid_run_does_not_fail_the_run()
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        Console.SetOut(new StringWriter());
        Console.SetError(new StringWriter());
        try
        {
            var engine = new QueryEngine(new FailingToolThenAnswerModel(), Array.Empty<ITool>(), workingDirectory: Path.GetTempPath());
            Assert.Equal(0, await HeadlessRunner.RunAsync(engine, "hi", CancellationToken.None));
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    [Fact]
    public async Task Strips_thinking_status_marker_split_across_deltas()
    {
        var stdout = await CaptureStdoutAsync(new SplitMarkerModel(
            "Hello. ", "__THINK", "ING_STATUS__:Analyz", "ing files... ", "The answer is 42."));

        Assert.DoesNotContain("THINKING_STATUS", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Hello.", stdout, StringComparison.Ordinal);
        Assert.Contains("The answer is 42.", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Strips_think_block_split_across_deltas()
    {
        var stdout = await CaptureStdoutAsync(new SplitMarkerModel(
            "<thi", "nk>secret reasoning</th", "ink>Final answer."));

        Assert.DoesNotContain("secret reasoning", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("<think", stdout, StringComparison.Ordinal);
        Assert.Contains("Final answer.", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Emits_nothing_when_answer_is_only_a_marker()
    {
        var stdout = await CaptureStdoutAsync(new SplitMarkerModel("__THINKING_STATUS__:Reasoning... (3s)"));

        Assert.Equal(string.Empty, stdout.Trim());
    }

    [Fact]
    public async Task Plain_text_passes_through_with_trailing_newline()
    {
        var stdout = await CaptureStdoutAsync(new SplitMarkerModel("hello ", "world"));

        Assert.Equal("hello world" + Environment.NewLine, stdout);
    }
}
