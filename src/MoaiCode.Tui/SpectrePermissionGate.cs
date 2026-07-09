using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;
using Spectre.Console;

namespace MoaiCode.Tui;

/// <summary>
/// 대화형 권한 다이얼로그. 비-읽기전용 툴 실행 전 사용자에게 허용 여부를 묻는다.
/// "항상 허용"은 세션 동안 해당 스코프(예: Bash(git status))만 자동 승인한다.
/// 스코프를 안전하게 좁힐 수 없는 호출(복합 셸 명령, rm/sudo 등)엔 항상 허용을 제공하지 않는다.
/// </summary>
public sealed class SpectrePermissionGate : IPermissionGate
{
    private readonly HashSet<string> _alwaysAllow = new(StringComparer.Ordinal);
    private readonly bool _offerAlways;

    /// <param name="offerAlways">
    /// false 면 "항상 허용"을 제공하지 않는다(원격 실행/파괴적 명령 확인 전용 게이트).
    /// </param>
    public SpectrePermissionGate(bool offerAlways = true) => _offerAlways = offerAlways;

    public ValueTask<bool> AllowAsync(ITool tool, ToolUseBlock call, CancellationToken ct)
    {
        var scope = PermissionRule.TryScope(tool, call);
        if (scope is not null && _alwaysAllow.Contains(scope))
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

        var canAlways = _offerAlways && scope is not null;
        var choices = canAlways
            ? new[] { "허용 (한 번)", $"항상 허용 (이 세션) — {scope}", "거부" }
            : new[] { "허용 (한 번)", "거부" };

        // 화살표 선택 위젯(SelectList). Spectre SelectionPrompt 는 단일 파일에서 크래시하므로 미사용.
        var choice = SelectList.Prompt($"툴 {tool.Name} 실행을 허용할까요?", choices);

        if (choice == 0)
        {
            return ValueTask.FromResult(true);
        }

        if (canAlways && choice == 1)
        {
            _alwaysAllow.Add(scope!);
            return ValueTask.FromResult(true);
        }

        return ValueTask.FromResult(false); // 거부 / 취소(-1)
    }
}
