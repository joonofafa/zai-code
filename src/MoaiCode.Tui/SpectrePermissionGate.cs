using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;
using Spectre.Console;

namespace MoaiCode.Tui;

/// <summary>
/// 대화형 권한 다이얼로그. 비-읽기전용 툴 실행 전 사용자에게 허용 여부를 묻는다.
/// "항상 허용"은 세션 동안 해당 툴을 자동 승인 (TS의 allow-rule 축약).
/// </summary>
public sealed class SpectrePermissionGate : IPermissionGate
{
    private readonly HashSet<string> _alwaysAllow = new(StringComparer.Ordinal);

    public ValueTask<bool> AllowAsync(ITool tool, ToolUseBlock call, CancellationToken ct)
    {
        if (_alwaysAllow.Contains(tool.Name))
        {
            return ValueTask.FromResult(true);
        }

        AnsiConsole.WriteLine();
        var display = ToolDisplay.Describe(tool.Name, call.Input);
        if (display.Length > 2000)
        {
            display = display[..2000] + "\n… (생략)";
        }

        var panel = new Panel(new Markup(Markup.Escape(display)))
            .Header($"[yellow]권한 요청[/] · [bold]{Markup.Escape(tool.Name)}[/]")
            .BorderColor(Color.Yellow);
        AnsiConsole.Write(panel);

        // 화살표 선택 위젯(SelectList). Spectre SelectionPrompt 는 단일 파일에서 크래시하므로 미사용.
        var choice = SelectList.Prompt(
            $"툴 {tool.Name} 실행을 허용할까요?",
            new[] { "허용 (한 번)", "항상 허용 (이 세션)", "거부" });

        switch (choice)
        {
            case 1: // 항상 허용
                _alwaysAllow.Add(tool.Name);
                return ValueTask.FromResult(true);
            case 0: // 한 번 허용
                return ValueTask.FromResult(true);
            default: // 거부 / 취소(-1)
                return ValueTask.FromResult(false);
        }
    }
}
