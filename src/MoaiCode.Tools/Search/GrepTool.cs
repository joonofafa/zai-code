using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MoaiCode.Core.Tools;

namespace MoaiCode.Tools.Search;

/// <summary>
/// 정규식 콘텐츠 검색 (TS GrepTool 대응, 현재 .NET regex 사용).
/// Phase 3+ 성능 개선 시 ripgrep 바이너리 번들 + 프로세스 래퍼로 교체 예정.
/// </summary>
public sealed class GrepTool : ITool
{
    private const int MaxMatches = 200;
    // 매칭 라인 수만으로는 컨텍스트를 못 지킨다 — 미니파이/락파일의 한 줄이 수십 KB일 수 있어
    // 라인당 길이와 총 출력량에도 상한을 둔다 (컨텍스트 폭주 방지).
    private const int MaxLineLength = 240;
    private const int MaxTotalChars = 30000;
    // 거대 텍스트 파일(.json/.log/.csv/덤프 등 바이너리 확장자 필터를 못 거른 것)을 통째로 읽다 멈추는 것 방지.
    private const long MaxFileBytes = 5_000_000;
    private static readonly TimeSpan ScanBudget = TimeSpan.FromSeconds(20);

    public string Name => "Grep";

    public string Description => """
        A powerful content search tool.

        Usage:
        - ALWAYS use Grep for content-search tasks. NEVER invoke grep or rg as a Bash command — the Grep tool has correct permissions and access.
        - Supports full regular expression syntax (e.g., "log.*Error", "function\s+\w+").
        - Set ignore_case for case-insensitive search. Provide path to scope the search (defaults to the working directory).
        - Returns matching lines as path:line:match.
        - Use the Agent tool for open-ended searches requiring multiple rounds.
        """;

    public bool IsReadOnly => true;
    public bool IsConcurrencySafe => true;

    public JsonElement InputSchema { get; } = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "pattern": { "type": "string", "description": "Regular expression" },
            "path": { "type": "string", "description": "Root directory (default: working dir)" },
            "ignore_case": { "type": "boolean" }
          },
          "required": ["pattern"]
        }
        """);

    private sealed record Input(
        [property: JsonPropertyName("pattern")] string? Pattern,
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("ignore_case")] bool IgnoreCase = false);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        var inp = input.Deserialize<Input>();
        if (inp is null || string.IsNullOrWhiteSpace(inp.Pattern))
        {
            yield return new ToolOutput("Grep: 'pattern' is required", IsError: true);
            yield break;
        }

        Regex rx;
        var opts = RegexOptions.Compiled | (inp.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        var pattern = inp.Pattern;
        var bad = false;
        try
        {
            rx = new Regex(pattern, opts);
        }
        catch (ArgumentException)
        {
            rx = null!;
            bad = true;
        }

        if (bad)
        {
            yield return new ToolOutput($"Invalid regex: {pattern}", IsError: true);
            yield break;
        }

        var root = ToolSchema.ResolvePath(context.WorkingDirectory, inp.Path ?? ".");
        if (!Directory.Exists(root) && !File.Exists(root))
        {
            yield return new ToolOutput($"Path not found: {root}", IsError: true);
            yield break;
        }

        var sb = new StringBuilder();
        var hits = 0;
        var walker = new FileWalker(ScanBudget);

        foreach (var file in walker.Walk(root, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (IsLikelyBinary(file))
            {
                continue;
            }

            try
            {
                if (new FileInfo(file).Length > MaxFileBytes)
                {
                    continue; // 너무 큰 파일은 스킵 (행 방지)
                }
            }
            catch (IOException)
            {
                continue;
            }

            string[] lines;
            try
            {
                lines = await File.ReadAllLinesAsync(file, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                continue;
            }

            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            for (var i = 0; i < lines.Length; i++)
            {
                if (rx.IsMatch(lines[i]))
                {
                    sb.Append(rel).Append(':').Append(i + 1).Append(':').AppendLine(Clip(lines[i]));
                    hits++;

                    if (sb.Length >= MaxTotalChars)
                    {
                        sb.AppendLine($"… (output truncated at {MaxTotalChars} chars; refine the pattern or narrow the path)");
                        goto done;
                    }

                    if (hits >= MaxMatches)
                    {
                        sb.AppendLine($"… (stopped at {MaxMatches} matches)");
                        goto done;
                    }
                }
            }
        }

    done:
        if (walker.TruncatedByTime)
        {
            sb.AppendLine($"… (search stopped after {ScanBudget.TotalSeconds:0}s; narrow the path or pattern)");
        }

        yield return new ToolOutput(hits == 0 && sb.Length == 0 ? "(no matches)" : sb.ToString());
    }

    // 매칭 라인을 트림하고 너무 길면 잘라낸다 (미니파이 한 줄이 컨텍스트를 삼키는 것 방지).
    private static string Clip(string line)
    {
        var s = line.Trim();
        return s.Length <= MaxLineLength
            ? s
            : s[..MaxLineLength] + $"… (+{s.Length - MaxLineLength} chars)";
    }

    private static bool IsLikelyBinary(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".dll" or ".exe" or ".pdb" or ".png" or ".jpg" or ".jpeg"
            or ".gif" or ".zip" or ".gz" or ".bin" or ".so" or ".dylib" or ".ico" or ".pdf";
    }
}
