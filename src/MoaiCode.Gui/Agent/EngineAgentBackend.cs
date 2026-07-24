using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using MoaiCode.Core.Agent;

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
    private static (string Doing, string Label) Phrase(string tool) => tool switch
    {
        // COM Office 실시간 편집(Desktop)
        "PowerPointInspect" => ("슬라이드 읽는 중", "슬라이드 읽기"),
        "PowerPointEdit" => ("슬라이드 편집 중", "슬라이드 편집"),
        "ExcelInspect" => ("시트 읽는 중", "시트 읽기"),
        "ExcelEdit" => ("시트 편집 중", "시트 편집"),
        "WordInspect" => ("문서 읽는 중", "문서 읽기"),
        "WordEdit" => ("문서 편집 중", "문서 편집"),
        // 문서 생성
        "DocxCreate" => ("워드 문서 만드는 중", "워드 문서 생성"),
        "XlsxCreate" => ("엑셀 문서 만드는 중", "엑셀 문서 생성"),
        "PptxCreate" => ("발표자료 만드는 중", "발표자료 생성"),
        "ImageCreate" => ("이미지 만드는 중", "이미지 생성"),
        "OfficeDocInspect" => ("문서 살펴보는 중", "문서 점검"),
        // 조직 문서함 / 검색
        "OrgDocsUpload" => ("회사 문서함에 올리는 중", "업로드"),
        "OrgDocs" or "OrgDocsList" => ("회사 문서함 검색하는 중", "문서함 검색"),
        "OrgList" => ("소속 조직 확인하는 중", "조직 확인"),
        "ChunkBuild" => ("문서 분석하는 중", "문서 분석"),
        "ChunkFetch" or "ChunkSearch" => ("문서 내용 찾는 중", "내용 검색"),
        "WebSearch" or "WebFetch" => ("웹 자료 찾는 중", "웹 검색"),
        // 파일
        "FileRead" or "Glob" or "Grep" => ("파일 확인하는 중", "파일 확인"),
        "FileWrite" or "FileEdit" => ("파일 저장하는 중", "파일 저장"),
        _ => ("작업하는 중", "작업"),
    };

    private static string FriendlyStart(string tool) => $"{Phrase(tool).Doing}…";

    private static string FriendlyDone(string tool, bool isError)
    {
        var label = Phrase(tool).Label;
        return isError ? $"{label} 실패" : $"{label} 완료";
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
            "XlsxCreate" => ("Icon.FileSpreadsheet", "엑셀"),
            "PptxCreate" => ("Icon.Presentation", "발표"),
            _ => ("Icon.FileText", "워드"),
        };
        doc = new DocumentProduced(icon, kind, file, path);
        return true;
    }
}
