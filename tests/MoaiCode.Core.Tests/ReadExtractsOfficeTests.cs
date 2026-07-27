using System.Text;
using System.Text.Json;
using MoaiCode.Core.Tools;
using MoaiCode.Tools.Files;
using MoaiCode.Tools.OpenXml;
using Xunit;

namespace MoaiCode.Core.Tests;

// Read 툴이 Office/PDF 를 내장 추출기로 투명하게 읽는지(외부 도구·스크립트 없이).
public sealed class ReadExtractsOfficeTests : IDisposable
{
    private readonly string _dir;

    public ReadExtractsOfficeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "moai-read-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task<string> Run(ITool tool, object input)
    {
        var ctx = new ToolContext(_dir, PermissionMode.Auto);
        var json = JsonSerializer.SerializeToElement(input);
        var sb = new StringBuilder();
        await foreach (var p in tool.ExecuteAsync(json, ctx, CancellationToken.None))
        {
            if (p is ToolOutput o)
            {
                Assert.False(o.IsError, o.Text);
                sb.Append(o.Text);
            }
        }

        return sb.ToString();
    }

    [Fact]
    public async Task Read_extracts_docx_as_plain_text()
    {
        await Run(new DocxCreateTool(), new
        {
            path = "r.docx",
            title = "월간 보고서",
            blocks = new object[]
            {
                new { type = "heading", level = 1, text = "개요" },
                new { type = "paragraph", text = "핵심 지표는 **양호**하다." },
            },
        });

        var outp = await Run(new FileReadTool(), new { path = "r.docx" });

        // 추출된 텍스트가 라인번호 포맷으로 보여야 한다(원시 zip 바이트가 아니라).
        Assert.Contains("월간 보고서", outp);
        Assert.Contains("개요", outp);
        Assert.Contains("핵심 지표는 양호하다.", outp); // 인라인 마커는 평문화됨
        Assert.DoesNotContain("PK", outp);   // zip 시그니처가 새어나오지 않음
    }
}
