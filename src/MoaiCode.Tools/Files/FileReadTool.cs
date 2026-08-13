using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoaiCode.Core.Agent.Prompts;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;

namespace MoaiCode.Tools.Files;

/// <summary>파일 읽기. 라인 번호가 붙은 내용을 반환 (편집 정확도를 위해 cat -n 스타일).</summary>
public sealed class FileReadTool : ITool
{
    private const int DefaultLimit = 2000;

    public string Name => "Read";

    public string Description => """
        Reads a file from the local filesystem. You can access any file directly by using this tool.

        Usage:
        - The path should be an absolute path, or relative to the working directory.
        - By default, it reads up to 2000 lines from the beginning of the file.
        - When you already know which part of the file you need, only read that part using offset/limit. This matters for larger files.
        - Results are returned using cat -n format, with line numbers starting at 1.
        - Office documents (.docx/.xlsx/.pptx) and .pdf are read as extracted plain text — read them directly with this tool, do NOT install packages or write scripts to parse them. (Scanned/image-only PDFs may yield little or no text.)
        - This tool reads text files, not directories. To list a directory, use Glob or Bash (ls).
        - If you read a file that exists but is empty, you will receive a system reminder warning in place of file contents.
        """;

    public bool IsReadOnly => true;
    public bool IsConcurrencySafe => true;

    public JsonElement InputSchema { get; } = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Absolute or working-dir-relative path" },
            "offset": { "type": "integer", "description": "1-based line to start from" },
            "limit": { "type": "integer", "description": "Max lines to read" }
          },
          "required": ["path"]
        }
        """);

    private sealed record Input(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("offset")] int? Offset,
        [property: JsonPropertyName("limit")] int? Limit);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        var inp = input.Deserialize<Input>();
        if (inp is null || string.IsNullOrWhiteSpace(inp.Path))
        {
            yield return new ToolOutput("Read: 'path' is required", IsError: true);
            yield break;
        }

        var path = ToolSchema.ResolvePath(context.WorkingDirectory, inp.Path);
        if (!File.Exists(path))
        {
            yield return new ToolOutput($"File not found: {path}", IsError: true);
            yield break;
        }

        context.Reads?.MarkRead(path);

        string text;
        var truncatedBytes = false;
        string? readError = null;

        // Office(.docx/.xlsx/.pptx)·PDF 는 바이너리(zip/pdf)라 원시 바이트 읽기로는 깨진다.
        // 내장 추출기로 평문 텍스트를 얻어 동일한 라인 포맷으로 반환한다(외부 도구·스크립트 불필요).
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".docx" or ".xlsx" or ".pptx" or ".pdf")
        {
            try
            {
                text = MoaiCode.Tools.OpenXml.DocumentTextExtractor.Extract(path);
            }
            catch (Exception ex)
            {
                readError = $"Read: could not extract text from {ext} — {ex.Message}";
                text = string.Empty;
            }
        }
        else
        {
            // 바운드 읽기: 특수 파일(FIFO·/dev/zero 등)이나 과대 파일을 통째로 읽다 무한 행/OOM 되는 것 방지.
            // 바이트 상한(10MB) + 시간 상한(30s). 사용자 Ctrl+C(ct)는 그대로 전파.
            const long maxBytes = 10_000_000;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

            try
            {
                await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var buf = new byte[maxBytes + 1];
                var total = 0;
                int n;
                while (total < buf.Length &&
                       (n = await fs.ReadAsync(buf.AsMemory(total, buf.Length - total), timeoutCts.Token)
                           .ConfigureAwait(false)) > 0)
                {
                    total += n;
                }

                if (total > maxBytes)
                {
                    truncatedBytes = true;
                    total = (int)maxBytes;
                }

                text = Encoding.UTF8.GetString(buf, 0, total);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                readError = L10n.Get("tools.files.readTimeout", path);
                text = string.Empty;
            }
            catch (IOException ex)
            {
                readError = $"Read: {ex.Message}";
                text = string.Empty;
            }
        }

        if (readError is not null)
        {
            yield return new ToolOutput(readError, IsError: true);
            yield break;
        }

        var normalized = text.ReplaceLineEndings("\n");
        var lines = normalized.Length == 0 ? Array.Empty<string>() : normalized.Split('\n');
        // 마지막 개행이 만드는 빈 항목 제거 (File.ReadAllLines 동작과 일치).
        if (lines.Length > 0 && lines[^1].Length == 0)
        {
            Array.Resize(ref lines, lines.Length - 1);
        }

        var start = Math.Max(0, (inp.Offset ?? 1) - 1);
        var limit = inp.Limit ?? DefaultLimit;

        var sb = new StringBuilder();
        var end = Math.Min(lines.Length, start + limit);
        for (var i = start; i < end; i++)
        {
            sb.Append((i + 1).ToString().PadLeft(6)).Append('\t').AppendLine(lines[i]);
        }

        if (end < lines.Length)
        {
            sb.Append($"… ({lines.Length - end} more lines)");
        }

        if (truncatedBytes)
        {
            sb.Append("\n… (file exceeded 10MB; read was truncated)");
        }

        // 빈 파일은 내용 대신 경고 리마인더로 대체 (OpenClaude 동작).
        if (sb.Length == 0)
        {
            yield return new ToolOutput(Reminders.EmptyFile);
            yield break;
        }

        // 파일 읽을 때마다 멀웨어 분석 경계 리마인더를 결과에 첨부.
        yield return new ToolOutput(sb + "\n\n" + Reminders.Malware);
    }
}
