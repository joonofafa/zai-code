using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MoaiCode.Localization;

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

        yield return new ActivityStarted(L10n.Get("gui.stub.understanding"));
        await Task.Delay(450, ct);
        yield return new ActivityDone(L10n.Get("gui.stub.understood"));

        yield return new ActivityStarted(L10n.Get("gui.stub.creatingFmt", kind));
        await Task.Delay(950, ct);
        yield return new ActivityDone(L10n.Get("gui.stub.createdFmt", kind));

        yield return new DocumentProduced(icon, kind, file, L10n.Get("gui.stub.docPathFmt", file));

        var reply = L10n.Get("gui.stub.replyFmt", kind);
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
            return ("Icon.FileSpreadsheet", L10n.Get("gui.stub.kindExcel"), L10n.Get("gui.stub.fileExcel"));
        }

        if (p.Contains("발표") || p.Contains("ppt") || p.Contains("슬라이드"))
        {
            return ("Icon.Presentation", L10n.Get("gui.stub.kindSlides"), L10n.Get("gui.stub.fileSlides"));
        }

        return ("Icon.FileText", L10n.Get("gui.stub.kindWord"), L10n.Get("gui.stub.fileWord"));
    }
}
