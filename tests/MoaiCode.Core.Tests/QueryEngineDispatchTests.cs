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
}
