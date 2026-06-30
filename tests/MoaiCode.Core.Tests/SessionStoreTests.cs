using System.Collections.Immutable;
using System.Text.Json;
using MoaiCode.Core.Messages;
using MoaiCode.Persistence;
using Xunit;

namespace MoaiCode.Core.Tests;

public class SessionStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly SessionStore _store;

    public SessionStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "occs-sess-" + Guid.NewGuid().ToString("n"));
        _store = new SessionStore(_dir);
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

    [Fact]
    public async Task Roundtrips_all_message_types_with_polymorphism()
    {
        using var argsDoc = JsonDocument.Parse("""{"command":"ls"}""");
        var original = new List<Message>
        {
            new SystemMessage("sys prompt"),
            new UserMessage("hello"),
            new AssistantMessage(ImmutableList.Create<ContentBlock>(
                new TextBlock("running a tool"),
                new ToolUseBlock("call_1", "Bash", argsDoc.RootElement.Clone()))),
            new ToolResultMessage("call_1", "file1\nfile2", IsError: false),
        };

        await _store.SaveAsync("s1", original);
        var loaded = await _store.LoadAsync("s1");

        Assert.Equal(4, loaded.Count);
        Assert.IsType<SystemMessage>(loaded[0]);
        Assert.Equal("hello", Assert.IsType<UserMessage>(loaded[1]).Text);

        var asst = Assert.IsType<AssistantMessage>(loaded[2]);
        Assert.Equal(2, asst.Content.Count);
        Assert.Equal("running a tool", Assert.IsType<TextBlock>(asst.Content[0]).Text);
        var tu = Assert.IsType<ToolUseBlock>(asst.Content[1]);
        Assert.Equal("Bash", tu.Name);
        Assert.Equal("ls", tu.Input.GetProperty("command").GetString());

        var tr = Assert.IsType<ToolResultMessage>(loaded[3]);
        Assert.Equal("call_1", tr.ToolUseId);
        Assert.Contains("file1", tr.Output);
    }

    [Fact]
    public async Task Load_missing_session_returns_empty()
    {
        var loaded = await _store.LoadAsync("does-not-exist");
        Assert.Empty(loaded);
    }

    [Fact]
    public async Task Lists_saved_sessions()
    {
        await _store.SaveAsync("a", new List<Message> { new UserMessage("x") });
        await _store.SaveAsync("b", new List<Message> { new UserMessage("y") });

        var sessions = _store.ListSessions();
        Assert.Contains("a", sessions);
        Assert.Contains("b", sessions);
    }
}
