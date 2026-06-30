using MoaiCode.Core.Agent;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;
using MoaiCode.Providers;
using Xunit;

namespace MoaiCode.Core.Tests;

public class MessageTests
{
    [Fact]
    public void UserMessage_carries_role_and_text()
    {
        var m = new UserMessage("hi");
        Assert.Equal(MessageRole.User, m.Role);
        Assert.Equal("hi", m.Text);
        Assert.False(string.IsNullOrEmpty(m.Id));
    }

    [Fact]
    public async Task QueryEngine_streams_echo_reply_and_completes()
    {
        var engine = new QueryEngine(new EchoChatModel(), Array.Empty<ITool>());
        engine.Seed(new[] { new SystemMessage("sys") });

        var text = "";
        var completed = false;
        await foreach (var ev in engine.SubmitAsync("ping"))
        {
            switch (ev)
            {
                case TextDelta d:
                    text += d.Text;
                    break;
                case TurnCompleted:
                    completed = true;
                    break;
            }
        }

        Assert.Contains("echo: ping", text);
        Assert.True(completed);
    }
}
