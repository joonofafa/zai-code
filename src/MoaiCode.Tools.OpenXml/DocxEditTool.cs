using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;

namespace MoaiCode.Tools.OpenXml;

/// <summary>
/// 기존 Word 문서(.docx)의 텍스트를 치환(템플릿 채우기). 서식을 보존하며, 플레이스홀더가 여러 run 으로
/// 쪼개진 경우(split runs)도 처리한다 — Anthropic/grok docx 스킬 replace_text 접근의 C# 이식.
/// </summary>
public sealed class DocxEditTool : ITool
{
    public string Name => "DocxEdit";

    public string Description => """
        Edits an EXISTING .docx by replacing text occurrences (template filling), preserving the
        document's formatting. Use this to fill a company/report template: provide "path" to the .docx
        and a list of "replacements" (each: find → with). Handles placeholders split across runs, and
        (by default) also replaces inside headers and footers. Case-insensitive by default.
        Prefer this over recreating a document when a template file already exists — recreating loses
        the template's styles, headers/footers, and decorative elements. For creating a NEW document
        from scratch, use DocxCreate. For editing an OPEN document on Windows, use the COM tools.
        """;

    public bool IsReadOnly => false;
    public bool IsConcurrencySafe => false;

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Existing .docx path (relative to workspace)" },
            "replacements": {
              "type": "array",
              "description": "Text replacements applied in order. Each occurrence of 'find' becomes 'with'.",
              "items": {
                "type": "object",
                "properties": {
                  "find": { "type": "string", "description": "Text/placeholder to find (e.g. '{{name}}' or 'Name Surname')" },
                  "with": { "type": "string", "description": "Replacement text" }
                },
                "required": ["find", "with"]
              }
            },
            "matchCase": { "type": "boolean", "description": "Case-sensitive match. Default false." },
            "includeHeadersFooters": { "type": "boolean", "description": "Also replace inside headers/footers. Default true." }
          },
          "required": ["path", "replacements"]
        }
        """).RootElement.Clone();

    private sealed record Replacement(
        [property: JsonPropertyName("find")] string? Find,
        [property: JsonPropertyName("with")] string? With);

    private sealed record Input(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("replacements")] List<Replacement>? Replacements,
        [property: JsonPropertyName("matchCase")] bool? MatchCase,
        [property: JsonPropertyName("includeHeadersFooters")] bool? IncludeHeadersFooters);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        var inp = input.Deserialize<Input>();
        var reps = (inp?.Replacements ?? new List<Replacement>())
            .Where(r => !string.IsNullOrEmpty(r.Find))
            .ToList();
        if (inp is null || string.IsNullOrWhiteSpace(inp.Path) || reps.Count == 0)
        {
            yield return new ToolOutput(L10n.Get("tools.docxEdit.inputRequired"), IsError: true);
            yield break;
        }

        string full;
        string? error = null;
        var count = 0;
        try
        {
            full = OpenXmlPaths.ResolveForRead(context.WorkingDirectory, inp.Path);
            if (!File.Exists(full))
            {
                error = L10n.Get("tools.docxEdit.notFound", inp.Path);
            }
            else
            {
                count = Apply(full, reps, inp.MatchCase == true, inp.IncludeHeadersFooters != false);
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            full = string.Empty;
        }

        yield return error is not null
            ? new ToolOutput(L10n.Get("tools.docxEdit.failed", error), IsError: true)
            : new ToolOutput(L10n.Get("tools.docxEdit.ok", full, count));
    }

    private static int Apply(string path, List<Replacement> reps, bool matchCase, bool includeHf)
    {
        // 원본 보호: 임시 복사본에서 수정한 뒤 원자적 교체 — 수정/저장 중 실패해도 원본이 깨지지 않는다.
        var tmp = path + ".tmp";
        File.Copy(path, tmp, overwrite: true);
        var total = 0;
        try
        {
            using (var doc = WordprocessingDocument.Open(tmp, isEditable: true))
            {
                var main = doc.MainDocumentPart ?? throw new InvalidOperationException("no main part");

                foreach (var para in main.Document.Body?.Descendants<Paragraph>() ?? Enumerable.Empty<Paragraph>())
                {
                    total += ReplaceInParagraph(para, reps, matchCase);
                }

                if (includeHf)
                {
                    foreach (var hp in main.HeaderParts)
                    {
                        foreach (var para in hp.Header.Descendants<Paragraph>())
                        {
                            total += ReplaceInParagraph(para, reps, matchCase);
                        }
                    }

                    foreach (var fp in main.FooterParts)
                    {
                        foreach (var para in fp.Footer.Descendants<Paragraph>())
                        {
                            total += ReplaceInParagraph(para, reps, matchCase);
                        }
                    }
                }

                if (total > 0)
                {
                    main.Document.Save();
                }
            }

            if (total > 0)
            {
                File.Move(tmp, path, overwrite: true);
            }
            else
            {
                try { File.Delete(tmp); } catch { } // 변경이 없으면 원본을 그대로 둔다.
            }
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }

        return total;
    }

    // 한 문단 안에서 치환. 2단계: (1) 단일 Text 노드 내 치환(서식 완전 보존),
    // (2) run 경계를 넘는(split) 매치는 문단 텍스트를 첫 Text 노드로 합쳐 치환(첫 run 서식 적용).
    private static int ReplaceInParagraph(Paragraph para, List<Replacement> reps, bool matchCase)
    {
        var texts = para.Descendants<Text>().ToList();
        if (texts.Count == 0)
        {
            return 0;
        }

        var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var n = 0;

        // 1단계: 노드 내부 치환.
        foreach (var t in texts)
        {
            foreach (var r in reps)
            {
                t.Text = ReplaceCount(t.Text, r.Find!, r.With ?? string.Empty, cmp, out var c);
                n += c;
            }
        }

        // 2단계: 노드 경계를 넘는 매치(현재 텍스트 기준으로 재확인).
        var full = string.Concat(texts.Select(t => t.Text));
        var spanning = reps.Any(r => full.IndexOf(r.Find!, cmp) >= 0);
        if (spanning)
        {
            var merged = full;
            foreach (var r in reps)
            {
                merged = ReplaceCount(merged, r.Find!, r.With ?? string.Empty, cmp, out var c);
                n += c;
            }

            texts[0].Text = merged;
            texts[0].Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve;
            for (var i = 1; i < texts.Count; i++)
            {
                texts[i].Text = string.Empty;
            }
        }

        return n;
    }

    private static string ReplaceCount(string input, string find, string with, StringComparison cmp, out int count)
    {
        count = 0;
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(find))
        {
            return input;
        }

        var sb = new System.Text.StringBuilder(input.Length);
        var i = 0;
        while (true)
        {
            var idx = input.IndexOf(find, i, cmp);
            if (idx < 0)
            {
                sb.Append(input, i, input.Length - i);
                break;
            }

            sb.Append(input, i, idx - i);
            sb.Append(with);
            i = idx + find.Length;
            count++;
        }

        return count > 0 ? sb.ToString() : input;
    }
}
