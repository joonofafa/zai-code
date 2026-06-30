using System.Text.Json;

namespace MoaiCode.Mcp;

/// <summary>.mcp.json / mcp.json 파싱 (Claude Code 호환: mcpServers 객체).</summary>
public static class McpConfigLoader
{
    private static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>working dir와 사용자 홈에서 설정 파일을 찾아 병합 (이름 충돌 시 먼저 발견 우선).</summary>
    public static IReadOnlyList<McpServerConfig> Discover(string workingDirectory)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(workingDirectory, ".mcp.json"),
            Path.Combine(workingDirectory, ".claude", "mcp.json"),
            Path.Combine(home, ".moai", "mcp.json"),
            Path.Combine(home, ".claude", "mcp.json"),
        };

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<McpServerConfig>();
        foreach (var path in candidates)
        {
            foreach (var cfg in LoadFromFile(path))
            {
                if (seen.Add(cfg.Name))
                {
                    result.Add(cfg);
                }
            }
        }

        return result;
    }

    public static IReadOnlyList<McpServerConfig> LoadFromFile(string path)
    {
        if (!File.Exists(path))
        {
            return Array.Empty<McpServerConfig>();
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), Options);
            return Parse(doc.RootElement);
        }
        catch (JsonException)
        {
            return Array.Empty<McpServerConfig>();
        }
    }

    public static IReadOnlyList<McpServerConfig> Parse(JsonElement root)
    {
        var result = new List<McpServerConfig>();
        if (!root.TryGetProperty("mcpServers", out var servers) || servers.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var prop in servers.EnumerateObject())
        {
            var o = prop.Value;
            var command = o.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "";
            if (command.Length == 0)
            {
                continue;
            }

            var args = new List<string>();
            if (o.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in a.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        args.Add(item.GetString()!);
                    }
                }
            }

            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            if (o.TryGetProperty("env", out var e) && e.ValueKind == JsonValueKind.Object)
            {
                foreach (var kv in e.EnumerateObject())
                {
                    if (kv.Value.ValueKind == JsonValueKind.String)
                    {
                        env[kv.Name] = kv.Value.GetString()!;
                    }
                }
            }

            result.Add(new McpServerConfig(prop.Name, command, args, env));
        }

        return result;
    }
}
