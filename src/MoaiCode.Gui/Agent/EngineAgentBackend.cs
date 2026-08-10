using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using MoaiCode.Core.Agent;
using MoaiCode.Localization;

namespace MoaiCode.Gui.Agent;

/// <summary>
/// 실제 코어(QueryEngine)를 구동하는 백엔드. SubmitAsync 의 StreamEvent 를 GUI 의 AgentEvent 로 매핑한다.
/// 추론 마커는 텍스트 런을 모아 ThinkFilter 로 제거(HeadlessRunner 와 동일 방식).
/// </summary>
public sealed class EngineAgentBackend : IAgentBackend
{
    private static readonly Regex DocPath =
        new(@"([^\s'""]+\.(?:docx|xlsx|pptx))", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly QueryEngine _engine;
    private readonly string _workspace;

    public EngineAgentBackend(QueryEngine engine, string workspace)
    {
        _engine = engine;
        _workspace = workspace;
    }

    public async IAsyncEnumerable<AgentEvent> SendAsync(
        string prompt, [EnumeratorCancellation] CancellationToken ct)
    {
        var run = new StringBuilder();

        await foreach (var ev in _engine.SubmitAsync(prompt, ct))
        {
            switch (ev)
            {
                case TextDelta d:
                    run.Append(d.Text);
                    break;

                case ToolCallRequested t:
                    var pre = Flush(run);
                    if (pre is not null) { yield return new AssistantDelta(pre); }
                    yield return new ActivityStarted(FriendlyStart(t.Block.Name));
                    break;

                case ToolExecuted x:
                    yield return new ActivityDone(FriendlyDone(x.ToolName, x.IsError));
                    if (!x.IsError && TryDocument(x.ToolName, x.Output, out var doc))
                    {
                        yield return doc!;
                    }

                    break;

                case TurnCompleted:
                    var post = Flush(run);
                    if (post is not null) { yield return new AssistantDelta(post); }
                    yield return new TurnDone();
                    break;
            }
        }
    }

    private static string? Flush(StringBuilder run)
    {
        if (run.Length == 0)
        {
            return null;
        }

        var clean = ThinkFilter.Strip(run.ToString());
        run.Clear();
        return clean.Length > 0 ? clean : null;
    }

    // (진행 중 문구, 완료/실패에 붙일 라벨) — 도구별로 구체적으로.
    // 시작: "{Doing}…"  완료: "{Label} 완료"  실패: "{Label} 실패"
    private static (string Doing, string Label) Phrase(string tool)
    {
        // 도구 → 로컬라이제이션 phrase id. (Doing/Label 은 gui.prog.<id>.doing/.label)
        var id = tool switch
        {
            // COM Office 실시간 편집(Desktop)
            "PowerPointInspect" => "slideRead",
            "PowerPointEdit" => "slideEdit",
            "ExcelInspect" => "sheetRead",
            "ExcelEdit" => "sheetEdit",
            "WordInspect" => "docRead",
            "WordEdit" => "docEdit",
            // 문서 생성
            "DocxCreate" => "docxCreate",
            "XlsxCreate" => "xlsxCreate",
            "PptxCreate" => "pptxCreate",
            "ImageCreate" => "imageCreate",
            "OfficeDocInspect" => "officeInspect",
            // 조직 문서함 / 검색
            "OrgDocsUpload" => "orgUpload",
            "OrgDocs" or "OrgDocsList" => "orgSearch",
            "OrgList" => "orgList",
            "ChunkBuild" => "chunkBuild",
            "ChunkFetch" or "ChunkSearch" => "chunkSearch",
            "WebSearch" or "WebFetch" => "webSearch",
            // 파일
            "FileRead" or "Glob" or "Grep" => "fileCheck",
            "FileWrite" or "FileEdit" => "fileSave",
            _ => "generic",
        };

        return (L10n.Get($"gui.prog.{id}.doing"), L10n.Get($"gui.prog.{id}.label"));
    }

    private static string FriendlyStart(string tool) => $"{Phrase(tool).Doing}…";

    private static string FriendlyDone(string tool, bool isError)
    {
        var label = Phrase(tool).Label;
        return isError ? L10n.Get("gui.prog.failedFmt", label) : L10n.Get("gui.prog.doneFmt", label);
    }

    private bool TryDocument(string tool, string output, out DocumentProduced? doc)
    {
        doc = null;
        if (tool is not ("DocxCreate" or "XlsxCreate" or "PptxCreate"))
        {
            return false;
        }

        var m = DocPath.Match(output ?? string.Empty);
        if (!m.Success)
        {
            return false;
        }

        var path = m.Groups[1].Value;
        if (!System.IO.Path.IsPathRooted(path))
        {
            path = System.IO.Path.Combine(_workspace, path); // [열기]용 절대경로
        }

        var file = System.IO.Path.GetFileName(path);
        var (icon, kind) = tool switch
        {
            "XlsxCreate" => ("Icon.FileSpreadsheet", L10n.Get("gui.kind.excel")),
            "PptxCreate" => ("Icon.Presentation", L10n.Get("gui.kind.slides")),
            _ => ("Icon.FileText", L10n.Get("gui.kind.word")),
        };
        doc = new DocumentProduced(icon, kind, file, path);
        return true;
    }
}
