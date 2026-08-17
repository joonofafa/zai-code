using System.Runtime.CompilerServices;
using System.Text.Json;
using MoaiCode.Core.Agent;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;
using MoaiCode.Tools.Files;
using Xunit;

namespace MoaiCode.Core.Tests;

public class QueryEngineDispatchTests : IDisposable
{
    private readonly string _dir;

    public QueryEngineDispatchTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "occs-disp-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }

    /// <summary>1턴차: Write 툴 호출 요청 → 2턴차: 텍스트 응답 후 종료.</summary>
    private sealed class ScriptedModel : IChatModel
    {
        private int _turn;

        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            IReadOnlyList<Message> messages,
            IReadOnlyList<ITool> tools,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            if (_turn++ == 0)
            {
                using var doc = JsonDocument.Parse("""{"path":"out.txt","content":"done"}""");
                yield return new ToolCallRequested(new ToolUseBlock("call_1", "Write", doc.RootElement.Clone()));
                yield return new TurnCompleted(new Usage(5, 3), "tool_calls");
            }
            else
            {
                yield return new TextDelta("파일을 작성했습니다.");
                yield return new TurnCompleted(new Usage(8, 4), "stop");
            }
        }
    }

    [Fact]
    public async Task Dispatches_tool_then_continues_to_final_text()
    {
        var engine = new QueryEngine(
            new ScriptedModel(),
            new ITool[] { new FileWriteTool() },
            workingDirectory: _dir);

        var toolExecuted = false;
        var finalText = "";
        var completedStop = "";
        await foreach (var ev in engine.SubmitAsync("write out.txt"))
        {
            switch (ev)
            {
                case ToolExecuted x:
                    toolExecuted = !x.IsError;
                    break;
                case TextDelta d:
                    finalText += d.Text;
                    break;
                case TurnCompleted c:
                    completedStop = c.StopReason;
                    break;
            }
        }

        Assert.True(toolExecuted);
        Assert.True(File.Exists(Path.Combine(_dir, "out.txt")));
        Assert.Equal("done", await File.ReadAllTextAsync(Path.Combine(_dir, "out.txt")));
        Assert.Contains("작성", finalText);
        Assert.Equal("stop", completedStop);
    }

    /// <summary>
    /// 1턴차: 툴 호출을 function-call 이 아니라 본문 텍스트 마크업으로 뱉고 stop=stop 으로 종료
    /// (GLM 계열 글리치) → 2턴차: 제대로 툴 호출.
    /// </summary>
    private sealed class RawMarkupThenToolModel : IChatModel
    {
        private int _turn;

        public IReadOnlyList<Message> LastMessages { get; private set; } = Array.Empty<Message>();

        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            IReadOnlyList<Message> messages,
            IReadOnlyList<ITool> tools,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            LastMessages = messages.ToList();
            switch (_turn++)
            {
                case 0:
                    yield return new TextDelta(
                        "Bash<arg_key>command</arg_key><arg_value>ls -la</arg_value></tool_call>");
                    yield return new TurnCompleted(new Usage(5, 3), "stop");
                    break;
                case 1:
                    using (var doc = JsonDocument.Parse("""{"path":"out.txt","content":"done"}"""))
                    {
                        yield return new ToolCallRequested(
                            new ToolUseBlock("call_1", "Write", doc.RootElement.Clone()));
                    }

                    yield return new TurnCompleted(new Usage(5, 3), "tool_calls");
                    break;
                default:
                    yield return new TextDelta("파일을 작성했습니다.");
                    yield return new TurnCompleted(new Usage(8, 4), "stop");
                    break;
            }
        }
    }

    [Fact]
    public async Task Raw_tool_call_markup_is_retried_not_returned_as_answer()
    {
        var model = new RawMarkupThenToolModel();
        var logs = new List<string>();
        var engine = new QueryEngine(model, new ITool[] { new FileWriteTool() },
            workingDirectory: _dir, log: logs.Add);

        var toolExecuted = false;
        await foreach (var ev in engine.SubmitAsync("list files"))
        {
            if (ev is ToolExecuted x)
            {
                toolExecuted |= !x.IsError;
            }
        }

        // 마크업만 뱉고 끝나면 안 된다 — 재요청되어 실제 툴이 돌아야 한다.
        Assert.True(toolExecuted, "logs:\n" + string.Join("\n", logs));
        Assert.True(File.Exists(Path.Combine(_dir, "out.txt")));

        // 마크업 텍스트는 히스토리에 남기지 않는다(모델이 자기 출력을 보고 형식을 반복하는 것 방지).
        var history = string.Join("\n", model.LastMessages
            .OfType<AssistantMessage>()
            .SelectMany(a => a.Content.OfType<TextBlock>().Select(t => t.Text)));
        Assert.DoesNotContain("<arg_key>", history);
    }
}
