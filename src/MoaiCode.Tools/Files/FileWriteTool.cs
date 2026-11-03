using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;

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
            yield return new ToolOutput(L10n.Get("tools.files.writeDenied", deny, path), IsError: true);
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

        // 기존 파일의 BOM 인코딩(UTF-8 BOM·UTF-16)은 유지한다 — 덮어쓰기로 인코딩이 바뀌면 Windows 도구가 깨진다.
        var encoding = TextFileCodec.Detect(File.Exists(path) ? ReadHead(path) : []).Encoding;
        var before = context.Changes is null ? null : await ReadBeforeAsync(path, ct).ConfigureAwait(false);
        await File.WriteAllBytesAsync(path, TextFileCodec.Encode(inp.Content ?? "", encoding), ct).ConfigureAwait(false);
        if (before is not { Text: null, Existed: true })   // 텍스트로 못 읽는 기존 파일은 /diff 에 넣지 않는다
        {
            context.Changes?.Record(path, before?.Text, inp.Content ?? "");
        }
        // 방금 쓴 내용은 모델이 안다 — 읽은 것으로 표시해 바로 Edit/재Write 할 수 있게 한다
        // (아니면 매번 Read 한 번을 더 해야 했다).
        context.Reads?.MarkRead(path);
        yield return new ToolOutput($"Wrote {(inp.Content ?? "").Length} bytes to {path}");
    }

    // /diff 용 덮어쓰기 전 내용. 없던 파일이면 Text=null·Existed=false, 텍스트가 아니거나 너무 크면 Text=null·Existed=true.
    private static async Task<(string? Text, bool Existed)?> ReadBeforeAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return (null, false);
        }

        try
        {
            if (new FileInfo(path).Length > MoaiCode.Core.Diff.FileChangeJournal.MaxContentChars * 4L)
            {
                return (null, true);
            }

            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            return TextFileCodec.TryDecodeStrict(bytes, out var text, out _) ? (text, true) : (null, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, true);
        }
    }

    private static byte[] ReadHead(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var head = new byte[3];
            var n = fs.Read(head, 0, head.Length);
            return head[..n];
        }
        catch (IOException)
        {
            return [];
        }
    }
}
