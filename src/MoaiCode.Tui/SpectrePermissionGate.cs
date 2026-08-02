using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;
using Spectre.Console;

namespace MoaiCode.Tui;

/// <summary>
/// 대화형 권한 다이얼로그. 비-읽기전용 툴 실행 전 사용자에게 허용 여부를 묻는다.
/// "항상 허용"은 명령 prefix 스코프(예: Bash(ssh moai-ec2))로 좁혀 규칙 저장소에 영속시킨다
/// (다음 실행에도 유지). 스코프를 안전하게 좁힐 수 없는 호출엔 항상 허용을 제공하지 않는다.
/// </summary>
public sealed class SpectrePermissionGate : IPermissionGate
{
    private readonly bool _offerAlways;
    private readonly Action<string>? _persistAllow;

    /// <param name="offerAlways">false 면 "항상 허용"을 제공하지 않는다.</param>
    /// <param name="persistAllow">"항상 허용" 선택 시 스코프 패턴을 저장하는 콜백(규칙 저장소에 영속).</param>
    public SpectrePermissionGate(bool offerAlways = true, Action<string>? persistAllow = null)
    {
        _offerAlways = offerAlways;
        _persistAllow = persistAllow;
    }

    public ValueTask<bool> AllowAsync(ITool tool, ToolUseBlock call, CancellationToken ct)
    {
        AnsiConsole.WriteLine();
        var display = ToolDisplay.Describe(tool.Name, call.Input);
        if (display.Length > 2000)
        {
            display = display[..2000] + L10n.Get("permission.truncated");
        }

        var panel = new Panel(new Markup(Markup.Escape(display)))
            .Header($"[yellow]{Markup.Escape(L10n.Get("permission.header"))}[/] · [bold]{Markup.Escape(tool.Name)}[/]")
            .BorderColor(Color.Yellow);
        AnsiConsole.Write(panel);

        var scope = _offerAlways ? PermissionRule.TryScope(tool, call) : null;
        var canAlways = scope is not null;
        // 정확 일치(Bash(=cmd))는 '이 명령 그대로'라고 표기, prefix 스코프는 그 패턴을 보여준다.
        var alwaysLabel = scope is not null && scope.StartsWith("Bash(=", StringComparison.Ordinal)
            ? L10n.Get("permission.alwaysSaveThis")
            : L10n.Get("permission.alwaysSaveScope", scope!);
        var allowOnce = L10n.Get("permission.allowOnce");
        var deny = L10n.Get("permission.deny");
        var choices = canAlways
            ? new[] { allowOnce, alwaysLabel, deny }
            : new[] { allowOnce, deny };

        // 화살표 선택 위젯(SelectList). Spectre SelectionPrompt 는 단일 파일에서 크래시하므로 미사용.
        var choice = SelectList.Prompt(L10n.Get("permission.prompt", tool.Name), choices);

        if (choice == 0)
        {
            return ValueTask.FromResult(true);
        }

        if (canAlways && choice == 1)
        {
            _persistAllow?.Invoke(scope!);
            return ValueTask.FromResult(true);
        }

        return ValueTask.FromResult(false); // 거부 / 취소(-1)
    }
}
