using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MoaiCode.Core.Tools;
using MoaiCode.Tools.OpenXml;
using Xunit;

namespace MoaiCode.OpenXml.Tests;

public sealed class DocxEditToolTests : IDisposable
{
    private readonly string _dir;
    public DocxEditToolTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "moai-docxedit-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private async Task<string> Edit(object input)
    {
        var ctx = new ToolContext(_dir, PermissionMode.Auto);
        var json = JsonSerializer.SerializeToElement(input);
        var sb = new System.Text.StringBuilder();
        await foreach (var p in new DocxEditTool().ExecuteAsync(json, ctx, CancellationToken.None))
        {
            if (p is ToolOutput o)
            {
                Assert.False(o.IsError, o.Text);
                sb.Append(o.Text);
            }
        }

        return sb.ToString();
    }

    private static string AllText(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        return string.Concat(doc.MainDocumentPart!.Document.Body!.Descendants<Text>().Select(t => t.Text));
    }

    // 단일 run 안의 플레이스홀더 치환 — DocxCreate 로 만든 문서(런이 안 쪼개짐).
    [Fact]
    public async Task Replaces_placeholders_in_single_runs()
    {
        var ctx = new ToolContext(_dir, PermissionMode.Auto);
        var create = JsonSerializer.SerializeToElement(new
        {
            path = "t.docx",
            blocks = new object[]
            {
                new { type = "paragraph", text = "담당자: {{name}}" },
                new { type = "paragraph", text = "일자: {{date}}" },
            },
        });
        await foreach (var _ in new DocxCreateTool().ExecuteAsync(create, ctx, CancellationToken.None)) { }

        var msg = await Edit(new
        {
            path = "t.docx",
            replacements = new object[]
            {
                new { find = "{{name}}", with = "홍길동" },
                new { find = "{{date}}", with = "2026-08-13" },
            },
        });

        var text = AllText(Path.Combine(_dir, "t.docx"));
        Assert.Contains("홍길동", text);
        Assert.Contains("2026-08-13", text);
        Assert.DoesNotContain("{{name}}", text);
        Assert.DoesNotContain("{{date}}", text);
    }

    // 분할 run(split runs): 플레이스홀더가 "{{na" + "me}}" 로 쪼개진 경우도 치환되어야 한다.
    [Fact]
    public async Task Replaces_placeholder_split_across_runs()
    {
        var path = Path.Combine(_dir, "split.docx");
        using (var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body(
                new Paragraph(
                    new Run(new Text("담당자: ") { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve }),
                    new Run(new Text("{{na")),
                    new Run(new Text("me}}")))));
            main.Document.Save();
        }

        await Edit(new
        {
            path = "split.docx",
            replacements = new object[] { new { find = "{{name}}", with = "김철수" } },
        });

        var text = AllText(path);
        Assert.Contains("김철수", text);
        Assert.DoesNotContain("{{na", text);
        Assert.DoesNotContain("me}}", text);
    }
}
