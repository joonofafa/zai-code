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
        // 무인 운영(opt-in) 타임아웃: 기본은 꺼짐(0) — 대화형 다이얼로그를 임의로 끊지 않는다.
        // env MOAI_PERMISSION_TIMEOUT 초 설정 시(예: headless 재활용/무인 tmux 세션) 만료를
        // 거부로 처리해 턴이 확인 대기로 영구 멈추는 것을 막는다. stream-json 게이트와 같은 env.
        var timeoutSec = PermissionTimeoutSeconds();
        if (timeoutSec > 0)
        {
            var decision = PromptWithTimeout(tool, call, timeoutSec, ct);
            return ValueTask.FromResult(decision);
        }

        return ValueTask.FromResult(Prompt(tool, call));
    }

    /// <summary>MOAI_PERMISSION_TIMEOUT 값(초). 0/무효면 비활성(무한 대기 — 기존 동작).</summary>
    internal static int PermissionTimeoutSeconds()
    {
        var v = Environment.GetEnvironmentVariable("MOAI_PERMISSION_TIMEOUT");
        return int.TryParse(v, out var n) && n > 0 ? n : 0;
    }

    private bool PromptWithTimeout(ITool tool, ToolUseBlock call, int timeoutSec, CancellationToken ct)
    {
        // 같은 스레드에서 그리고 기다리되 선택 위젯에 만료 토큰을 넘긴다. 예전엔 위젯을 백그라운드 태스크로
        // 돌려 타임아웃과 경주시켰는데, 만료 뒤에도 그 위젯이 살아남아 이후 키를 가로챘고(입력창·ESC 먹통)
        // 거기서 '항상 허용'이 눌리면 이미 거부된 호출의 허용 규칙이 저장됐다.
        using var expiry = CancellationTokenSource.CreateLinkedTokenSource(ct);
        expiry.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
        var allowed = Prompt(tool, call, expiry.Token);
        if (allowed || !expiry.IsCancellationRequested || ct.IsCancellationRequested)
        {
            return allowed;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[{TuiTheme.Mark(TuiTheme.Role.Warning)}]{Markup.Escape(L10n.Get("permission.timedOut", timeoutSec))}[/]");
        return false;   // fail-closed — stream-json 게이트와 동일 정책
    }

    private bool Prompt(ITool tool, ToolUseBlock call, CancellationToken expiry = default)
    {
        AnsiConsole.WriteLine();
        var display = ToolDisplay.Describe(tool.Name, call.Input);
        if (display.Length > 2000)
        {
            display = display[..2000] + L10n.Get("permission.truncated");
        }

        var panel = new Panel(new Markup(Markup.Escape(display)))
            .Header($"[{TuiTheme.Mark(TuiTheme.Role.Warning)}]{Markup.Escape(L10n.Get("permission.header"))}[/] · [bold]{Markup.Escape(tool.Name)}[/]")
            .BorderColor(TuiTheme.ColorOf(TuiTheme.Role.Warning));
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
        var choice = SelectList.Prompt(L10n.Get("permission.prompt", tool.Name), choices, cancel: expiry);

        if (choice == 0)
        {
            return true;
        }

        if (canAlways && choice == 1)
        {
            _persistAllow?.Invoke(scope!);
            return true;
        }

        return false; // 거부 / 취소(-1)
    }
}
