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
        try
        {
            await foreach (var ev in engine.SubmitAsync(prompt, ct).WithCancellation(ct))
            {
                switch (ev)
                {
                    case TextDelta d:
                        Console.Out.Write(d.Text);
                        break;
                    case ToolCallRequested t:
                        Console.Error.WriteLine($"→ {t.Block.Name}");
                        break;
                    case ToolExecuted x:
                        Console.Error.WriteLine($"{(x.IsError ? "✗" : "✓")} {x.ToolName}");
                        hadError |= x.IsError;
                        break;
                    case TurnCompleted:
                        break;
                }
            }
        }
        catch (ProviderException ex)
        {
            Console.Error.WriteLine($"provider error: {ex.Message}");
            return 2;
        }

        Console.Out.WriteLine();
        return hadError ? 1 : 0;
    }
}
