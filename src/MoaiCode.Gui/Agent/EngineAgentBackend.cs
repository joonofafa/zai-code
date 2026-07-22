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

    // (동사, 명사) — 시작/완료 문구를 자연스럽게 만들기 위해.
    private static (string Verb, string Noun) Phrase(string tool) => tool switch
    {
        "DocxCreate" => ("만드는 중", "워드 문서"),
        "XlsxCreate" => ("만드는 중", "엑셀 문서"),
        "PptxCreate" => ("만드는 중", "발표자료"),
        "OfficeDocInspect" => ("점검하는 중", "문서"),
        "OrgDocsUpload" => ("올리는 중", "회사 문서함에"),
        "OrgDocs" or "OrgDocsList" => ("검색하는 중", "회사 문서함"),
        "OrgList" => ("확인하는 중", "소속 조직"),
        "ChunkBuild" => ("분석하는 중", "문서"),
        "ChunkFetch" or "ChunkSearch" => ("찾는 중", "문서 내용"),
        "WebSearch" or "WebFetch" => ("찾는 중", "웹 자료"),
        "FileRead" or "Glob" or "Grep" => ("확인하는 중", "파일"),
        "FileWrite" or "FileEdit" => ("저장하는 중", "파일"),
        _ => ("작업하는 중", ""),
    };

    private static string FriendlyStart(string tool)
    {
        var (verb, noun) = Phrase(tool);
        return string.IsNullOrEmpty(noun) ? $"{verb}…" : $"{noun} {verb}…";
    }

    private static string FriendlyDone(string tool, bool isError)
    {
        var (_, noun) = Phrase(tool);
        var head = string.IsNullOrEmpty(noun) ? "작업" : noun.TrimEnd('에');
        return isError ? $"{head} 실패" : $"{head} 완료";
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
            "XlsxCreate" => ("📊", "엑셀"),
            "PptxCreate" => ("📑", "발표"),
            _ => ("📝", "워드"),
        };
        doc = new DocumentProduced(icon, kind, file, path);
        return true;
    }
}
