using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;

namespace MoaiCode.Tools.Files;

/// <summary>정확 문자열 치환 편집. old_string이 유일해야 함(replace_all 제외).</summary>
public sealed class FileEditTool : ITool
{
    public string Name => "Edit";
    public string Description => """
        Performs exact string replacements in files.

        Usage:
        - You should use the Read tool to read the file before editing it.
        - When editing text from Read output, preserve the exact indentation (tabs/spaces) as it appears AFTER the line-number prefix. The prefix format is: spaces + line number + tab. Never include any part of the line-number prefix in old_string or new_string.
        - ALWAYS prefer editing existing files. NEVER write new files unless explicitly required.
        - Only use emojis if the user explicitly requests it. Avoid adding emojis to files unless asked.
        - The edit will FAIL if old_string is not unique in the file. Either provide a larger string with more surrounding context to make it unique, or use replace_all to change every instance.
        - Use replace_all for replacing and renaming strings across the file (e.g. renaming a variable).
        """;

    public bool IsReadOnly => false;
    public bool IsConcurrencySafe => false;

    public JsonElement InputSchema { get; } = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string" },
            "old_string": { "type": "string" },
            "new_string": { "type": "string" },
            "replace_all": { "type": "boolean" }
          },
          "required": ["path", "old_string", "new_string"]
        }
        """);

    private sealed record Input(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("old_string")] string? OldString,
        [property: JsonPropertyName("new_string")] string? NewString,
        [property: JsonPropertyName("replace_all")] bool ReplaceAll = false);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        var inp = input.Deserialize<Input>();
        if (inp is null || string.IsNullOrWhiteSpace(inp.Path) || inp.OldString is null || inp.NewString is null)
        {
            yield return new ToolOutput("Edit: 'path', 'old_string', 'new_string' are required", IsError: true);
            yield break;
        }

        var path = ToolSchema.ResolvePath(context.WorkingDirectory, inp.Path);

        // 하드 플로어: 시스템 임계 경로(/boot,/etc,...) 수정은 권한 모드와 무관하게 거부.
        if (PathSafety.DenyWriteReason(path) is { } deny)
        {
            yield return new ToolOutput(L10n.Get("tools.files.editDenied", deny, path), IsError: true);
            yield break;
        }

        if (!File.Exists(path))
        {
            yield return new ToolOutput($"File not found: {path}", IsError: true);
            yield break;
        }

        if (context.Reads is { } reads && !reads.WasRead(path))
        {
            yield return new ToolOutput(
                "You must use the Read tool to read this file before editing it.", IsError: true);
            yield break;
        }

        var content = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var count = CountOccurrences(content, inp.OldString);
        if (count == 0)
        {
            yield return new ToolOutput("old_string not found in file", IsError: true);
            yield break;
        }

        if (count > 1 && !inp.ReplaceAll)
        {
            yield return new ToolOutput(
                $"old_string is not unique ({count} matches). Set replace_all or add context.", IsError: true);
            yield break;
        }

        var updated = inp.ReplaceAll
            ? content.Replace(inp.OldString, inp.NewString)
            : ReplaceFirst(content, inp.OldString, inp.NewString);

        await File.WriteAllTextAsync(path, updated, ct).ConfigureAwait(false);
        yield return new ToolOutput($"Edited {path} ({(inp.ReplaceAll ? count : 1)} replacement(s))");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (needle.Length == 0)
        {
            return 0;
        }

        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) != -1)
        {
            count++;
            idx += needle.Length;
        }

        return count;
    }

    private static string ReplaceFirst(string source, string oldValue, string newValue)
    {
        var idx = source.IndexOf(oldValue, StringComparison.Ordinal);
        return idx < 0 ? source : source[..idx] + newValue + source[(idx + oldValue.Length)..];
    }
}
