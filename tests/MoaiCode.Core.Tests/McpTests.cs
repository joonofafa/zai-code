using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using MoaiCode.Core.Tools;
using MoaiCode.Mcp;
using Xunit;

namespace MoaiCode.Core.Tests;

public class McpTests
{
    /// <summary>인메모리 MCP 서버 — JSON-RPC 요청에 즉시 응답.</summary>
    private sealed class FakeMcpServer : IMcpTransport
    {
        private readonly Channel<string> _in = Channel.CreateUnbounded<string>();

        public ChannelReader<string> Incoming => _in.Reader;

        public Task SendAsync(string message, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
            {
                return Task.CompletedTask; // notification
            }

            var id = idEl.GetInt64();
            var method = root.GetProperty("method").GetString();

            JsonNode result = method switch
            {
                "initialize" => new JsonObject
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["serverInfo"] = new JsonObject { ["name"] = "fake" },
                },
                "tools/list" => new JsonObject
                {
                    ["tools"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["name"] = "echo",
                            ["description"] = "echo tool",
                            ["inputSchema"] = new JsonObject { ["type"] = "object" },
                        },
                    },
                },
                "tools/call" => new JsonObject
                {
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = "called:" + root.GetProperty("params").GetProperty("name").GetString(),
                        },
                    },
                    ["isError"] = false,
                },
                _ => new JsonObject(),
            };

            var resp = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
            _in.Writer.TryWrite(resp.ToJsonString());
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _in.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Initialize_list_and_call_tools_over_jsonrpc()
    {
        await using var client = new McpClient(new FakeMcpServer());
        await client.InitializeAsync(default);

        var tools = await client.ListToolsAsync(default);
        Assert.Single(tools);
        Assert.Equal("echo", tools[0].Name);
        Assert.Equal("echo tool", tools[0].Description);

        var result = await client.CallToolAsync("echo", default, default);
        Assert.False(result.IsError);
        Assert.Contains("called:echo", result.Text);
    }

    [Fact]
    public async Task McpTool_wraps_server_tool_with_prefixed_name()
    {
        await using var client = new McpClient(new FakeMcpServer());
        await client.InitializeAsync(default);
        var info = (await client.ListToolsAsync(default))[0];

        var tool = new McpTool(client, "fs", info);
        Assert.Equal("mcp__fs__echo", tool.Name);

        using var emptyArgs = JsonDocument.Parse("{}");
        var sb = "";
        await foreach (var p in tool.ExecuteAsync(emptyArgs.RootElement, new ToolContext(".", PermissionMode.Auto), default))
        {
            if (p is ToolOutput o)
            {
                sb += o.Text;
            }
        }

        Assert.Contains("called:echo", sb);
    }

    [Fact]
    public void ConfigLoader_parses_mcpServers()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "mcpServers": {
                "filesystem": {
                  "command": "npx",
                  "args": ["-y", "@modelcontextprotocol/server-filesystem", "/data"],
                  "env": { "FOO": "bar" }
                },
                "no-command": { "args": ["x"] }
              }
            }
            """);

        var configs = McpConfigLoader.Parse(doc.RootElement);
        Assert.Single(configs); // command 없는 서버는 제외
        var fs = configs[0];
        Assert.Equal("filesystem", fs.Name);
        Assert.Equal("npx", fs.Command);
        Assert.Equal(3, fs.Args.Count);
        Assert.Equal("bar", fs.Env["FOO"]);
    }
}
