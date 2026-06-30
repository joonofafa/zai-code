using System.Runtime.CompilerServices;
using System.Text.Json;
using MoaiCode.Core.Agent;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;
using MoaiCode.Tools.Files;
using Xunit;

namespace MoaiCode.Core.Tests;

public class PermissionGateTests : IDisposable
{
    private readonly string _dir;

    public PermissionGateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "occs-perm-" + Guid.NewGuid().ToString("n"));
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

    /// <summary>1턴: 지정 툴 호출 → 2턴: 텍스트 종료.</summary>
    private sealed class OneToolModel(string toolName, string argsJson) : IChatModel
    {
        private int _turn;

        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            IReadOnlyList<Message> messages, IReadOnlyList<ITool> tools,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            if (_turn++ == 0)
            {
                using var d = JsonDocument.Parse(argsJson);
                yield return new ToolCallRequested(new ToolUseBlock("c1", toolName, d.RootElement.Clone()));
                yield return new TurnCompleted(new Usage(1, 1), "tool_calls");
            }
            else
            {
                yield return new TextDelta("done");
                yield return new TurnCompleted(new Usage(1, 1), "stop");
            }
        }
    }

    private sealed class DenyGate : IPermissionGate
    {
        public ValueTask<bool> AllowAsync(ITool tool, ToolUseBlock call, CancellationToken ct)
            => ValueTask.FromResult(false);
    }

    [Fact]
    public async Task Denied_write_tool_is_not_executed()
    {
        var model = new OneToolModel("Write", """{"path":"out.txt","content":"x"}""");
        var engine = new QueryEngine(
            model, new ITool[] { new FileWriteTool() }, new DenyGate(), workingDirectory: _dir);

        var denied = false;
        await foreach (var ev in engine.SubmitAsync("go"))
        {
            if (ev is ToolExecuted { IsError: true })
            {
                denied = true;
            }
        }

        Assert.True(denied);
        Assert.False(File.Exists(Path.Combine(_dir, "out.txt")));
    }

    [Fact]
    public async Task ReadOnly_tool_bypasses_deny_gate()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "r.txt"), "secret content");

        var model = new OneToolModel("Read", """{"path":"r.txt"}""");
        var engine = new QueryEngine(
            model, new ITool[] { new FileReadTool() }, new DenyGate(), workingDirectory: _dir);

        var executedOk = false;
        await foreach (var ev in engine.SubmitAsync("read it"))
        {
            if (ev is ToolExecuted { IsError: false } x && x.Output.Contains("secret content"))
            {
                executedOk = true;
            }
        }

        Assert.True(executedOk); // 읽기 전용 툴은 게이트를 우회해 실행됨
    }
}
