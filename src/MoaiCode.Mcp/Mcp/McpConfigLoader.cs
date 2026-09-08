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
            Path.Combine(home, ".zaicode", "mcp.json"),
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
                        env[kv.Name] = ExpandEnv(kv.Value.GetString()!);
                    }
                }
            }

            result.Add(new McpServerConfig(prop.Name, command, args, env));
        }

        return result;
    }

    /// <summary>
    /// env 값의 <c>${VAR}</c> 를 현재 프로세스 환경변수로 치환한다. 설정 파일에 API 키를
    /// 평문으로 적지 않고 런처가 주입한 값을 참조하기 위한 것. 정의되지 않은 이름은 빈 문자열이 된다
    /// (원문을 남기면 키인 줄 알고 그대로 서버에 넘어가 인증 오류가 더 헷갈려진다).
    /// </summary>
    internal static string ExpandEnv(string value)
    {
        if (value.IndexOf("${", StringComparison.Ordinal) < 0) return value;

        var sb = new System.Text.StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '$' && i + 1 < value.Length && value[i + 1] == '{')
            {
                var end = value.IndexOf('}', i + 2);
                if (end > i + 2)
                {
                    var name = value[(i + 2)..end];
                    sb.Append(Environment.GetEnvironmentVariable(name) ?? string.Empty);
                    i = end;
                    continue;
                }
            }
            sb.Append(value[i]);
        }
        return sb.ToString();
    }
}
