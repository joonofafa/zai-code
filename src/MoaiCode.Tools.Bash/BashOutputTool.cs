using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;

namespace MoaiCode.Tools.Bash;

/// <summary>
/// 백그라운드 셸(Bash run_in_background)의 새 출력을 읽는다 — 마지막 조회 이후 분만 증분 반환.
/// </summary>
public sealed class BashOutputTool : ITool
{
    private readonly BackgroundShellRegistry _registry;

    public BashOutputTool(BackgroundShellRegistry? registry = null)
    {
        _registry = registry ?? BackgroundShellRegistry.Shared;
    }

    public string Name => "BashOutput";

    public string Description => """
        Retrieves output from a background shell started with Bash (run_in_background: true).

        Returns only NEW output since the last BashOutput call for that shell, plus the shell status (running / completed with exit code / killed). Call it repeatedly to follow a long-running command such as a build, server, or log tail. Use `filter` (a regex applied per line) to keep only relevant lines — non-matching lines are still consumed.
        """;

    public bool IsReadOnly => true;
    public bool IsConcurrencySafe => true;

    public JsonElement InputSchema { get; } = Parse(
        """
        {
          "type": "object",
          "properties": {
            "shell_id": { "type": "string", "description": "Id returned by Bash when run_in_background was true (e.g. bash_1)" },
            "filter": { "type": "string", "description": "Optional regex; only lines matching it are returned" }
          },
          "required": ["shell_id"]
        }
        """);

    private sealed record Input(
        [property: JsonPropertyName("shell_id")] string? ShellId,
        [property: JsonPropertyName("filter")] string? Filter);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        var inp = input.Deserialize<Input>();
        if (inp is null || string.IsNullOrWhiteSpace(inp.ShellId))
        {
            yield return new ToolOutput("BashOutput: 'shell_id' is required", IsError: true);
            yield break;
        }

        var shell = _registry.Get(inp.ShellId.Trim());
        if (shell is null)
        {
            yield return new ToolOutput(L10n.Get("tools.bashOutput.notFound", inp.ShellId), IsError: true);
            yield break;
        }

        Regex? filter = null;
        if (!string.IsNullOrEmpty(inp.Filter))
        {
            filter = TryRegex(inp.Filter);
            if (filter is null)
            {
                yield return new ToolOutput(L10n.Get("tools.bashOutput.badFilter", inp.Filter), IsError: true);
                yield break;
            }
        }

        var (text, truncated) = shell.ReadNew();
        if (filter is not null && text.Length > 0)
        {
            text = string.Join('\n', text.Split('\n').Where(l => filter.IsMatch(l)));
        }

        var sb = new StringBuilder();
        sb.Append(StatusLine(shell));
        if (truncated)
        {
            sb.AppendLine().Append(L10n.Get("tools.bashOutput.truncated"));
        }

        sb.AppendLine().AppendLine();
        sb.Append(text.Length == 0 ? L10n.Get("tools.bashOutput.noNewOutput") : text.TrimEnd('\n'));
        yield return new ToolOutput(sb.ToString());
    }

    internal static string StatusLine(BackgroundShell shell) => shell.Status switch
    {
        BackgroundShellStatus.Running => L10n.Get("tools.bashOutput.statusRunning", shell.Id),
        BackgroundShellStatus.Killed => L10n.Get("tools.bashOutput.statusKilled", shell.Id),
        _ => L10n.Get("tools.bashOutput.statusCompleted", shell.Id, shell.ExitCode?.ToString() ?? "?"),
    };

    private static Regex? TryRegex(string pattern)
    {
        try
        {
            return new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
