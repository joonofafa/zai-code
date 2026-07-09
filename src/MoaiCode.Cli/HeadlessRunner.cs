using System.Text;
using MoaiCode.Core.Agent;
using MoaiCode.Providers;

namespace MoaiCode.Cli;

/// <summary>
/// 비대화형 1회 실행 (moai run). 텍스트는 stdout, 툴 진행은 stderr로 분리해
/// 파이프라인/CI 친화적 출력 제공.
/// </summary>
public static class HeadlessRunner
{
    public static async Task<int> RunAsync(QueryEngine engine, string prompt, CancellationToken ct)
    {
        var hadError = false;

        // 추론 마커(<think>…, __THINKING_STATUS__:…)는 델타 경계에 걸쳐 쪼개져 오므로 조각 단위로는
        // 지울 수 없다. 텍스트 런을 모아 ThinkFilter 를 적용한 뒤 내보낸다(예전엔 그대로 흘려보내
        // 파이프 출력에 마커가 답변과 한 줄로 붙어 나왔다).
        var run = new StringBuilder();

        void FlushText()
        {
            if (run.Length == 0)
            {
                return;
            }

            var clean = ThinkFilter.Strip(run.ToString());
            run.Clear();
            if (clean.Length > 0)
            {
                Console.Out.WriteLine(clean);
            }
        }

        try
        {
            await foreach (var ev in engine.SubmitAsync(prompt, ct).WithCancellation(ct))
            {
                switch (ev)
                {
                    case TextDelta d:
                        run.Append(d.Text);
                        break;
                    case ToolCallRequested t:
                        FlushText();
                        Console.Error.WriteLine($"→ {t.Block.Name}");
                        break;
                    case ToolExecuted x:
                        Console.Error.WriteLine($"{(x.IsError ? "✗" : "✓")} {x.ToolName}");
                        hadError |= x.IsError;
                        break;
                    case TurnCompleted:
                        FlushText();
                        break;
                }
            }
        }
        catch (ProviderException ex)
        {
            FlushText();
            Console.Error.WriteLine($"provider error: {ex.Message}");
            return 2;
        }

        FlushText();
        return hadError ? 1 : 0;
    }
}
