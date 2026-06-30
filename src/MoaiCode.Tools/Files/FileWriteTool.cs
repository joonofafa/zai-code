using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoaiCode.Core.Tools;

namespace MoaiCode.Tools.Files;

/// <summary>파일 쓰기(생성/덮어쓰기). 디렉토리 자동 생성.</summary>
public sealed class FileWriteTool : ITool
{
    public string Name => "Write";
    public string Description => """
        Writes a file to the local filesystem.

        Usage:
        - This tool will overwrite the existing file if there is one at the provided path.
        - If this is an existing file, you SHOULD use the Read tool first to read its contents.
        - Prefer the Edit tool for modifying existing files — it only sends the diff. Only use this tool to create new files or for complete rewrites.
        - NEVER create documentation files (*.md) or README files unless explicitly requested by the user.
        - Only use emojis if the user explicitly requests it. Avoid writing emojis to files unless asked.
        """;

    public bool IsReadOnly => false;
    public bool IsConcurrencySafe => false;

    public JsonElement InputSchema { get; } = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string" },
            "content": { "type": "string" }
          },
          "required": ["path", "content"]
        }
        """);

    private sealed record Input(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("content")] string? Content);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        var inp = input.Deserialize<Input>();
        if (inp is null || string.IsNullOrWhiteSpace(inp.Path))
        {
            yield return new ToolOutput("Write: 'path' is required", IsError: true);
            yield break;
        }

        var path = ToolSchema.ResolvePath(context.WorkingDirectory, inp.Path);

        // 하드 플로어: 시스템 임계 경로(/boot,/etc,...) 쓰기는 권한 모드와 무관하게 거부.
        if (PathSafety.DenyWriteReason(path) is { } deny)
        {
            yield return new ToolOutput($"Write 거부 — {deny}: {path}", IsError: true);
            yield break;
        }

        // 기존 파일 덮어쓰기 전에는 Read 필요 (신규 파일은 면제).
        if (File.Exists(path) && context.Reads is { } reads && !reads.WasRead(path))
        {
            yield return new ToolOutput(
                "This file exists — use the Read tool to read it before overwriting.", IsError: true);
            yield break;
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(path, inp.Content ?? "", ct).ConfigureAwait(false);
        yield return new ToolOutput($"Wrote {(inp.Content ?? "").Length} bytes to {path}");
    }
}
