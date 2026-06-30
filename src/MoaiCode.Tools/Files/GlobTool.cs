using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MoaiCode.Core.Tools;

namespace MoaiCode.Tools.Files;

/// <summary>glob 패턴으로 파일 열거. ** / * / ? 지원. node_modules/.git/bin/obj 자동 제외.</summary>
public sealed class GlobTool : ITool
{
    private const int MaxResults = 1000;
    private static readonly TimeSpan ScanBudget = TimeSpan.FromSeconds(20);

    public string Name => "Glob";

    public string Description => """
        - Fast file pattern matching tool that works with any codebase size
        - Supports glob patterns like "**/*.cs" or "src/**/*.ts"
        - Returns matching file paths
        - Use this tool when you need to find files by name patterns
        - When doing an open-ended search that may require multiple rounds of globbing and grepping, use the Agent tool instead
        """;

    public bool IsReadOnly => true;
    public bool IsConcurrencySafe => true;

    public JsonElement InputSchema { get; } = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "pattern": { "type": "string", "description": "Glob like **/*.cs" },
            "path": { "type": "string", "description": "Root directory (default: working dir)" }
          },
          "required": ["pattern"]
        }
        """);

    private sealed record Input(
        [property: JsonPropertyName("pattern")] string? Pattern,
        [property: JsonPropertyName("path")] string? Path);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();

        var inp = input.Deserialize<Input>();
        if (inp is null || string.IsNullOrWhiteSpace(inp.Pattern))
        {
            yield return new ToolOutput("Glob: 'pattern' is required", IsError: true);
            yield break;
        }

        var root = ToolSchema.ResolvePath(context.WorkingDirectory, inp.Path ?? ".");
        if (!Directory.Exists(root))
        {
            yield return new ToolOutput($"Directory not found: {root}", IsError: true);
            yield break;
        }

        var rx = GlobToRegex(inp.Pattern);
        var matches = new List<string>();
        var walker = new FileWalker(ScanBudget);
        foreach (var file in walker.Walk(root, ct))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rx.IsMatch(rel))
            {
                matches.Add(rel);
                if (matches.Count >= MaxResults)
                {
                    break;
                }
            }
        }

        matches.Sort(StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (var m in matches)
        {
            sb.AppendLine(m);
        }

        if (walker.TruncatedByTime)
        {
            sb.AppendLine($"… (search stopped after {ScanBudget.TotalSeconds:0}s; narrow the path or pattern)");
        }

        yield return new ToolOutput(
            matches.Count == 0 && !walker.TruncatedByTime ? "(no matches)" : $"{matches.Count} match(es):\n{sb}");
    }

    private static Regex GlobToRegex(string glob)
    {
        var normalized = glob.Replace('\\', '/');
        var sb = new StringBuilder("^");
        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < normalized.Length && normalized[i + 1] == '*')
                    {
                        sb.Append(".*");
                        i++;
                        if (i + 1 < normalized.Length && normalized[i + 1] == '/')
                        {
                            i++;
                        }
                    }
                    else
                    {
                        sb.Append("[^/]*");
                    }

                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase);
    }
}
