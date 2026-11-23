using System.Text.Json;
using MoaiCode.Cli;
using MoaiCode.Config;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Security;
using MoaiCode.Core.Tools;
using MoaiCode.Persistence;
using Xunit;

namespace MoaiCode.Core.Tests;

/// <summary>종료 관문(testGate) — 파일을 고친 뒤에는 테스트 명령이 통과해야 끝낼 수 있다.</summary>
public sealed class TestGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mate-testgate-" + Guid.NewGuid().ToString("n"));

    public TestGateTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class EditTool : ITool
    {
        public string Name => "Edit";
        public string Description => "";
        public bool IsReadOnly => false;
        public bool IsConcurrencySafe => false;
        public JsonElement InputSchema { get; } = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

        public IAsyncEnumerable<ToolProgress> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task BeforeStop_afterAnEdit_blocksUntilTheTestCommandPasses()
    {
        if (OperatingSystem.IsWindows())
        {
            return;   // 테스트 명령을 bash 문법으로 썼다
        }

        var settings = Settings.Default with { TestGate = true, TestCommand = "test -f ok.txt || { echo MISSING-OK; exit 3; }" };
        var observer = new HarnessToolObserver(settings, new CheckpointStore(_dir, Path.Combine(_dir, "cp")));
        var ctx = new ToolContext(_dir, PermissionMode.Auto);
        var call = new ToolUseBlock("c1", "Edit", JsonDocument.Parse("{}").RootElement.Clone());

        Assert.Null(await observer.BeforeStopAsync(ctx, default));            // 고친 것이 없으면 테스트를 돌리지 않는다

        await observer.AfterToolAsync(new EditTool(), call, ctx, "ok", isError: false, default);
        var blocked = await observer.BeforeStopAsync(ctx, default);
        Assert.NotNull(blocked);
        Assert.Contains("exited 3", blocked);
        Assert.Contains("MISSING-OK", blocked);

        File.WriteAllText(Path.Combine(_dir, "ok.txt"), "");
        Assert.Null(await observer.BeforeStopAsync(ctx, default));            // 통과하면 끝낼 수 있다

        File.Delete(Path.Combine(_dir, "ok.txt"));
        Assert.Null(await observer.BeforeStopAsync(ctx, default));            // 통과 뒤 새 수정이 없으면 다시 돌리지 않는다
    }

    [Fact]
    public async Task BeforeStop_withTheGateOff_neverBlocks()
    {
        var settings = Settings.Default with { TestCommand = "exit 1" };
        var observer = new HarnessToolObserver(settings, new CheckpointStore(_dir, Path.Combine(_dir, "cp")));
        var ctx = new ToolContext(_dir, PermissionMode.Auto);
        await observer.AfterToolAsync(new EditTool(), new ToolUseBlock("c1", "Edit", JsonDocument.Parse("{}").RootElement.Clone()),
            ctx, "ok", isError: false, default);

        Assert.Null(await observer.BeforeStopAsync(ctx, default));
        Assert.False(SettingsLoader.ApplyJson(Settings.Default, """{"testGate":true}""",
            allowSensitive: false, allowAutomation: false, allowPermissionRules: false).TestGate);   // 프로젝트 설정으로는 켜지 못한다
        Assert.True(SettingsLoader.ApplyJson(Settings.Default, """{"testGate":true}""").TestGate);
    }
}
