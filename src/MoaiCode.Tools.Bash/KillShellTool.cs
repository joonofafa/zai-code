using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;

namespace MoaiCode.Tools.Bash;

/// <summary>백그라운드 셸(Bash run_in_background)을 프로세스 트리째 종료한다.</summary>
public sealed class KillShellTool : ITool
{
    private readonly BackgroundShellRegistry _registry;

    public KillShellTool(BackgroundShellRegistry? registry = null)
    {
        _registry = registry ?? BackgroundShellRegistry.Shared;
    }

    public string Name => "KillShell";

    public string Description => """
        Kills a running background shell started with Bash (run_in_background: true), including its child processes.
        Use it to stop servers, watchers, or log tails you no longer need. Output already captured stays readable via BashOutput.
        """;

    public bool IsReadOnly => false;
    public bool IsConcurrencySafe => true;

    public JsonElement InputSchema { get; } = Parse(
        """
        {
          "type": "object",
          "properties": {
            "shell_id": { "type": "string", "description": "Id of the background shell to kill (e.g. bash_1)" }
          },
          "required": ["shell_id"]
        }
        """);

    private sealed record Input([property: JsonPropertyName("shell_id")] string? ShellId);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        var inp = input.Deserialize<Input>();
        if (inp is null || string.IsNullOrWhiteSpace(inp.ShellId))
        {
            yield return new ToolOutput("KillShell: 'shell_id' is required", IsError: true);
            yield break;
        }

        var shell = _registry.Get(inp.ShellId.Trim());
        if (shell is null)
        {
            yield return new ToolOutput(L10n.Get("tools.bashOutput.notFound", inp.ShellId), IsError: true);
            yield break;
        }

        if (!shell.Kill())
        {
            yield return new ToolOutput(L10n.Get("tools.killShell.alreadyDone", BashOutputTool.StatusLine(shell)));
            yield break;
        }

        yield return new ToolOutput(L10n.Get("tools.killShell.killed", shell.Id));
    }

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
