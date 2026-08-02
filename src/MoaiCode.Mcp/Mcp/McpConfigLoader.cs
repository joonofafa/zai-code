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

    /// <summary>
    /// 설정 파일을 찾아 병합. 사용자 홈(신뢰) 설정을 먼저 보므로 이름 충돌 시 사용자 MCP 가 우선한다.
    /// 보안(SEC-001): 프로젝트(작업 디렉터리) MCP 는 신뢰하지 않는 저장소가 시작 시 임의 프로세스를
    /// 실행하는 통로가 되므로, <paramref name="includeProjectScope"/> 가 true 일 때만 로드한다.
    /// </summary>
    public static IReadOnlyList<McpServerConfig> Discover(string workingDirectory, bool includeProjectScope = true)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // 사용자(신뢰) 설정을 먼저 → 이름 충돌 시 사용자 MCP 가 프로젝트 MCP 를 이긴다(shadowing 방지).
        var candidates = new List<string>
        {
            Path.Combine(home, ".moai", "mcp.json"),
            Path.Combine(home, ".claude", "mcp.json"),
        };
        if (includeProjectScope)
        {
            candidates.Add(Path.Combine(workingDirectory, ".mcp.json"));
            candidates.Add(Path.Combine(workingDirectory, ".claude", "mcp.json"));
        }

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

    /// <summary>작업 디렉터리에 프로젝트 MCP 설정 파일이 있는지(로드 스킵 시 사용자 안내용).</summary>
    public static bool HasProjectConfig(string workingDirectory)
        => File.Exists(Path.Combine(workingDirectory, ".mcp.json"))
           || File.Exists(Path.Combine(workingDirectory, ".claude", "mcp.json"));

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
