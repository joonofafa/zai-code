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

        // 인코딩·BOM 을 보존한다. 판별한 인코딩으로 온전히 풀리지 않는 파일(CP949 등)은 고치지 않는다 —
        // UTF-8 로 읽어 다시 쓰면 편집한 곳만이 아니라 파일의 모든 비-ASCII 글자가 깨진다.
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        if (!TextFileCodec.TryDecodeStrict(bytes, out var content, out var encoding))
        {
            yield return new ToolOutput(
                "This file is not valid UTF-8/UTF-16 text (a legacy encoding such as CP949?). Editing it would " +
                "corrupt every non-ASCII character, so nothing was changed. Convert it first (e.g. with iconv) " +
                "or edit it with a tool that knows its encoding.", IsError: true);
            yield break;
        }

        // Windows CRLF 파일 폴백: Read 도구는 줄 단위(\r 제거)로 보여주므로 모델이 만든
        // old_string/new_string 은 항상 LF-only 다. CRLF 파일에 그대로 ordinal 매칭하면
        // 무조건 실패한다("old_string not found" 연발 — 실제 사고). 파일에 CR 이 있고
        // 요청 문자열에 없으면 LF→CRLF 로 정규화해 매칭한다(치환 결과의 줄끝은 파일 규칙 유지).
        if (content.Contains('\r') && !inp.OldString.Contains('\r'))
        {
            var normOld = inp.OldString.Replace("\n", "\r\n");
            if (CountOccurrences(content, normOld) > 0)
            {
                inp = inp with
                {
                    OldString = normOld,
                    // new_string 에 CRLF 가 이미 섞여 있으면 그대로 바꾸면 \r\r\n 이 된다 — LF 로 맞춘 뒤 바꾼다.
                    NewString = inp.NewString.Replace("\r\n", "\n").Replace("\n", "\r\n"),
                };
            }
        }

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

        await File.WriteAllBytesAsync(path, TextFileCodec.Encode(updated, encoding), ct).ConfigureAwait(false);
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
