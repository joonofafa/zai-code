using MoaiCode.Mcp;
using Xunit;

namespace MoaiCode.Core.Tests;

// 보안(SEC-001): (1) 자식 MCP 프로세스로 민감 환경변수가 새지 않는지,
// (2) 프로젝트(작업 디렉터리) MCP 는 신뢰 opt-in 전까지 로드되지 않는지.
public class McpSecurityTests
{
    [Theory]
    [InlineData("OPENAI_API_KEY")]
    [InlineData("OPENAI_BASE_URL")]
    [InlineData("MOAI_LOGIN_HOST")]
    [InlineData("PROXY_PASSWORD")]
    [InlineData("ANTHROPIC_API_KEY")]
    [InlineData("AWS_SECRET_ACCESS_KEY")]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("some_credential")]
    public void Sensitive_env_keys_are_flagged(string key)
        => Assert.True(StdioTransport.IsSensitiveEnvKey(key));

    [Theory]
    [InlineData("PATH")]
    [InlineData("HOME")]
    [InlineData("TEMP")]
    [InlineData("LANG")]
    [InlineData("NODE_ENV")]
    public void Ordinary_env_keys_pass_through(string key)
        => Assert.False(StdioTransport.IsSensitiveEnvKey(key));

    [Fact]
    public void Project_mcp_is_not_loaded_unless_opted_in()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"moai-mcp-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".mcp.json"),
                """{ "mcpServers": { "evil": { "command": "python", "args": ["-c", "print(1)"] } } }""");

            Assert.True(McpConfigLoader.HasProjectConfig(dir));

            // 기본(신뢰 안 함): 프로젝트 MCP 는 로드되지 않는다.
            var gated = McpConfigLoader.Discover(dir, includeProjectScope: false);
            Assert.DoesNotContain(gated, c => c.Name == "evil");

            // opt-in: 명시적으로 신뢰하면 로드된다.
            var opted = McpConfigLoader.Discover(dir, includeProjectScope: true);
            Assert.Contains(opted, c => c.Name == "evil");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
