using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace MoaiCode.Gui.Agent;

/// <summary>
/// UI 개발용 흉내 백엔드 — 실제 엔진 없이 스트리밍/문서생성 흐름을 재현한다.
/// (실코어 배선 전, 화면을 눈으로 보며 다듬기 위한 것.)
/// </summary>
public sealed class StubAgentBackend : IAgentBackend
{
    public async IAsyncEnumerable<AgentEvent> SendAsync(
        string prompt, [EnumeratorCancellation] CancellationToken ct)
    {
        var (icon, kind, file) = Guess(prompt);

        yield return new ActivityStarted("요청을 이해하는 중…");
        await Task.Delay(450, ct);
        yield return new ActivityDone("요청 이해 완료");

        yield return new ActivityStarted($"{kind} 문서를 만드는 중…");
        await Task.Delay(950, ct);
        yield return new ActivityDone($"{kind} 문서 생성 완료");

        yield return new DocumentProduced(icon, kind, file, $"내 문서\\MoAI\\{file}");

        var reply = $"{kind} 문서로 정리했어요. [열기]로 확인하시고, 회사 문서함에 올리려면 " +
                    "[문서함 올리기]를 누르세요. 표·차트나 색상도 원하시면 말씀만 하세요.";
        foreach (var word in reply.Split(' '))
        {
            yield return new AssistantDelta(word + " ");
            await Task.Delay(28, ct);
        }

        yield return new TurnDone();
    }

    private static (string Icon, string Kind, string File) Guess(string prompt)
    {
        var p = prompt.ToLowerInvariant();
        if (p.Contains("엑셀") || p.Contains("표") || p.Contains("차트") || p.Contains("xlsx"))
        {
            return ("Icon.FileSpreadsheet", "엑셀", "매출_정리.xlsx");
        }

        if (p.Contains("발표") || p.Contains("ppt") || p.Contains("슬라이드"))
        {
            return ("Icon.Presentation", "발표", "발표자료.pptx");
        }

        return ("Icon.FileText", "워드", "보고서.docx");
    }
}
