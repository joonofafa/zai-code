using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MoaiCode.Core.Agent;
using MoaiCode.Core.Agent.Prompts;
using MoaiCode.Core.Messages;
using MoaiCode.Localization;
using MoaiCode.Tui.Commands;
using Spectre.Console;

namespace MoaiCode.Tui;

/// <summary>
/// 대화형 REPL. 대화형 터미널에서는 Spectre.Live로 assistant 응답을 증분 렌더하고,
/// 비대화형(파이프/리다이렉트)에서는 평문 스트리밍으로 폴백 (테스트/스크립트 호환).
/// 슬래시 명령은 SlashRegistry로 디스패치. 권한 다이얼로그는 QueryEngine이
/// IPermissionGate를 통해 호출 (Live 종료 후 안전한 시점).
/// </summary>
public sealed class ReplApp
{
    private readonly SlashContext _ctx;
    private readonly SlashRegistry _slash;
    private readonly bool _interactive;
    private readonly string _sessionId;
    private CancellationTokenSource? _activeTurnCts;
    private bool _producedOutputInTurn;   // 이번 턴에 화면에 뭔가 렌더됐는지(빈 응답 감지)
    private string? _brainstormSuggestion; // 브레인스토밍: AI가 이번 턴에 남긴 답변 제안(다음 입력 ghost)

    // 브레인스토밍 답변 제안 마커. AI 가 질문 뒤 마지막에 "[[SUGGEST]] <추천답>" 으로 남긴다.
    private const string SuggestMarker = "[[SUGGEST]]";

    // 표시 텍스트에서 제안 마커를 떼어내고 제안을 돌려준다(마커는 화면에 보이지 않게).
    private static (string Clean, string? Suggest) ExtractSuggest(string text)
    {
        var i = text.LastIndexOf(SuggestMarker, StringComparison.Ordinal);
        if (i < 0)
        {
            return (text, null);
        }

        var suggest = text[(i + SuggestMarker.Length)..].Trim().Trim('`', '"', '\'').Trim();
        var clean = text[..i].TrimEnd();
        return (clean, suggest.Length > 0 ? suggest : null);
    }

    // 타입어헤드: 턴 처리 중 친 입력을 모아 턴 종료 후 순차 제출. MOAI_TYPEAHEAD=0/false/off 로 끔(기본 on).
    private readonly TurnInputQueue _turnInput = new();
    private readonly bool _typeAhead =
        Environment.GetEnvironmentVariable("MOAI_TYPEAHEAD") is not ("0" or "false" or "off");

    // 턴 동안 하단 1행을 예약해 상시 입력바를 표시(스크롤 영역 사용). _barWanted=이번 턴이 원함, _barActive=영역 설정됨.
    private bool _barActive;
    private bool _barWanted;
    private readonly object _barLock = new();  // 바/스피너 콘솔 쓰기 직렬화(워처 즉시 갱신용)

    private readonly List<string> _history = new();
    private readonly bool _useRawEditor;
    private readonly BottomDock? _dock;   // 하단 고정 입력(opt-in: MOAI_BOTTOM_DOCK=1)
    private IReadOnlyList<string> _slashNames = Array.Empty<string>();

    public ReplApp(SlashContext ctx, SlashRegistry slash)
    {
        _ctx = ctx;
        _slash = slash;
        _interactive = !Console.IsInputRedirected;
        _sessionId = "sess-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");

        // raw 라인에디터(히스토리/자동완성). 문제 시 MOAI_SIMPLE_INPUT=1 로 평문 입력으로 폴백.
        var simple = Environment.GetEnvironmentVariable("MOAI_SIMPLE_INPUT");
        var disabled = !string.IsNullOrEmpty(simple)
            && !simple.Equals("0", StringComparison.Ordinal)
            && !simple.Equals("false", StringComparison.OrdinalIgnoreCase);
        _useRawEditor = _interactive && !disabled;

        // 하단 고정 입력창+상태줄 — 기본 활성. MOAI_BOTTOM_DOCK=0(또는 off/false)로 opt-out.
        // (MOAI_SIMPLE_INPUT 로 raw 에디터를 끄면 이 모드도 자동 비활성.)
        var dockEnv = Environment.GetEnvironmentVariable("MOAI_BOTTOM_DOCK");
        var dockOff = dockEnv is not null
            && (dockEnv.Equals("0", StringComparison.Ordinal)
                || dockEnv.Equals("false", StringComparison.OrdinalIgnoreCase)
                || dockEnv.Equals("off", StringComparison.OrdinalIgnoreCase));
        _dock = _useRawEditor && !dockOff && BottomDock.Fits() ? new BottomDock(BuildStatusLine) : null;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // 시그니처 배너: 그라데이션 ASCII (Banner.Render)
        Banner.Render();
        AnsiConsole.MarkupLine($"[yellow]MoAI Code — Enterprise Coding Agent (V {Banner.Version()})[/]");
        AnsiConsole.MarkupLine($"[grey70]{Markup.Escape(L10n.Get("repl.help"))}[/]");
        // 배너~프롬프트 사이 공백 2줄.
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine();

        if (_interactive)
        {
            Console.CancelKeyPress += OnCancelKeyPress;
        }

        // 슬래시 자동완성 후보 + 명령 히스토리(↑/↓) 시드.
        _slashNames = _slash.Commands.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        try
        {
            _history.AddRange(await _ctx.History.RecentAsync(100, ct).ConfigureAwait(false));
        }
        catch
        {
            // 히스토리 로드 실패는 무시.
        }

        try
        {
            await RunLoopAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _dock?.Teardown();   // 스크롤 영역 원복 (하단 고정 모드였다면)
            if (_interactive)
            {
                Console.CancelKeyPress -= OnCancelKeyPress;
            }
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var quit = false;
        while (!quit && !ct.IsCancellationRequested)
        {
            string? input;
            // 브레인스토밍: 이번 입력에 AI 추천 답을 희미한 ghost 로 띄운다(Tab 채택, 타이핑 시 사라짐).
            LineEditor.SeedGhost = _ctx.State.Brainstorming ? _brainstormSuggestion : null;

            if (_dock is not null)
            {
                // 하단 고정: 상태줄+입력창은 화면 맨 아래, 출력은 위 영역에서 스크롤.
                input = _dock.ReadLine(_history, _slashNames,
                    () => { CycleMode(); return BuildStatusLine(); },
                    _ctx.State.Brainstorming);
            }
            else if (_useRawEditor)
            {
                Func<string> cycle = () => { CycleMode(); return BuildStatusLine(); };
                // 상태줄을 프롬프트 위에 출력하는 단순 모드 (wrap 중복 없음).
                input = LineEditor.ReadLine(_history, _slashNames, cycle, BuildStatusLine, _ctx.State.Brainstorming);
            }
            else
            {
                AnsiConsole.Markup("[green]❯ [/]");
                input = Console.ReadLine();
            }

            if (input is null)
            {
                break;
            }

            // Shift+Tab: act ⇄ plan 모드 토글 후 프롬프트 재표시.
            if (input == LineEditor.CycleModeSignal)
            {
                CycleMode();
                continue;
            }

            quit = await ProcessInputAsync(input, ct).ConfigureAwait(false);

            // 타입어헤드: 턴 처리 중 사용자가 친 입력(큐)을 순차로 이어서 제출.
            while (!quit && _typeAhead && !ct.IsCancellationRequested)
            {
                _turnInput.CommitPartial();
                if (!_turnInput.TryDequeue(out var queued))
                {
                    break;
                }

                AnsiConsole.MarkupLine($"[grey58]↳ Queued[/] [green]❯[/] {Markup.Escape(queued)}");
                quit = await ProcessInputAsync(queued, ct).ConfigureAwait(false);
            }
        }
    }

    // 한 입력 라인 처리(슬래시/셸/도움말/에이전트 턴). REPL 종료면 true.
    private async Task<bool> ProcessInputAsync(string input, CancellationToken ct)
    {
        var trimmed = input.Trim();
        if (trimmed.Length > 0)
        {
            AddHistory(trimmed); // 히스토리(↑)에는 접힌 표식 그대로 — 다시 불러도 확장된다.
        }

        // 붙여넣기 표식을 원문으로 되돌린 뒤 모델/세션에 전달한다.
        var expanded = PasteStore.Expand(trimmed);

        if (expanded.StartsWith('/'))
        {
            return await HandleCommandAsync(expanded, ct).ConfigureAwait(false);
        }

        // '!' 접두: 사용자가 직접 친 셸 명령을 모델/권한 게이트를 거치지 않고 바로 실행하고,
        // 명령+출력을 대화 컨텍스트에 주입해 다음 턴에 모델이 참조할 수 있게 한다.
        if (expanded.StartsWith('!'))
        {
            await RunShellCommandAsync(expanded[1..].Trim(), ct).ConfigureAwait(false);
            return false;
        }

        // '?': 키맵/입력 문법 도움말.
        if (expanded == "?")
        {
            ShowKeymap();
            return false;
        }

        if (string.IsNullOrWhiteSpace(expanded))
        {
            return false;
        }

        // 위험 판정 분류기가 "이 명령이 사용자가 시킨 일인가"를 보려면 원문 요청이 필요하다.
        _ctx.State.LastUserRequest = expanded;

        await _ctx.History.AppendAsync(expanded, ct).ConfigureAwait(false);
        await AttachFileMentionsAsync(expanded, ct).ConfigureAwait(false);
        await ConsumeTurnAsync(expanded, ct).ConfigureAwait(false);
        return false;
    }

    private async Task<bool> HandleCommandAsync(string line, CancellationToken ct)
    {
        var parts = line[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var name = parts.Length > 0 ? parts[0] : "";
        var args = parts.Skip(1).ToArray();

        if (!_slash.TryGet(name, out var cmd))
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(L10n.Get("repl.unknownCommand", name))}[/]");
            return false;
        }

        SlashResult result;
        try
        {
            result = await cmd.ExecuteAsync(_ctx, args, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 슬래시 명령 하나의 오류가 REPL 전체를 죽이지 않게 한다.
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(L10n.Get("repl.commandError", name, ex.Message))}[/]");
            return false;
        }

        if (!string.IsNullOrEmpty(result.Output))
        {
            AnsiConsole.MarkupLine($"[grey70]{Markup.Escape(result.Output)}[/]");
        }

        // 프롬프트형 커맨드(/init, /review)는 결과 프롬프트로 에이전트 턴을 실행.
        if (!string.IsNullOrEmpty(result.SubmitPrompt))
        {
            await ConsumeTurnAsync(result.SubmitPrompt, ct).ConfigureAwait(false);
        }

        return result.Quit;
    }

    // '!' 셸 실행: 사용자가 직접 입력한 명령이므로 권한 게이트를 거치지 않고 현재 cwd 서브셸에서 실행한다.
    // stdout/stderr 를 콘솔에 그대로 흘리고, 명령+출력(캡)을 system-reminder 로 대화 컨텍스트에 주입한다.
    // cd 등은 서브셸 한정(세션 cwd 로 영속되지 않음).
    private async Task RunShellCommandAsync(string command, CancellationToken ct)
    {
        if (command.Length == 0)
        {
            AnsiConsole.MarkupLine("[grey70]Usage: ! <shell command>[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[grey58]$ {Markup.Escape(command)}[/]");

        var (file, shellArgs) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", command })
            : (File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh", new[] { "-c", command });

        var psi = new ProcessStartInfo
        {
            FileName = file,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Directory.GetCurrentDirectory(),
        };
        foreach (var a in shellArgs)
        {
            psi.ArgumentList.Add(a);
        }

        var captured = new StringBuilder();
        var sync = new object();
        void Sink(string? data, bool err)
        {
            if (data is null)
            {
                return;
            }

            lock (sync)
            {
                if (err)
                {
                    Console.Error.WriteLine(data);
                }
                else
                {
                    Console.WriteLine(data);
                }

                captured.AppendLine(data);
            }
        }

        int exit;
        try
        {
            using var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, e) => Sink(e.Data, false);
            proc.ErrorDataReceived += (_, e) => Sink(e.Data, true);
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            try
            {
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch
                {
                    // best-effort: 취소 시 자식 프로세스 정리.
                }

                throw;
            }

            exit = proc.ExitCode;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]! failed:[/] [grey70]{Markup.Escape(ex.Message)}[/]");
            return;
        }

        // 명령 + 출력(캡)을 컨텍스트에 주입 — 다음 턴에 모델이 이 실행 결과를 참조할 수 있다.
        var outText = QueryEngine.CapToolOutput(captured.ToString().TrimEnd(), 8000);
        var note = $"The user ran a shell command directly with '!':\n$ {command}\n(exit code {exit})\n"
                   + (outText.Length > 0 ? outText : "(no output)");
        _ctx.Engine.AddSystemReminder(note);
    }

    // '?': 키보드 단축키 + 입력 문법(/ ! @) 요약 표시.
    private static void ShowKeymap()
    {
        AnsiConsole.MarkupLine("[aqua]Keys & input[/]");
        var rows = new (string Key, string Desc)[]
        {
            ("/command", "run a slash command (Tab to autocomplete)"),
            ("!command", "run a shell command directly (output added to context)"),
            ("@path", "attach a file's contents to the conversation"),
            ("?", "show this help"),
            ("Enter", "submit"),
            ("Up / Down", "recall history"),
            ("Left / Right, Home / End", "move the cursor"),
            ("Backspace / Delete", "edit"),
            ("Shift+Tab", "toggle act / plan mode"),
            ("Ctrl+C", "cancel the current turn"),
        };
        foreach (var (key, desc) in rows)
        {
            AnsiConsole.MarkupLine($"  [white]{Markup.Escape(key).PadRight(26)}[/][grey70]{Markup.Escape(desc)}[/]");
        }

        AnsiConsole.MarkupLine("[grey58]Type /help for the full command list.[/]");
    }

    // '@path' 멘션 → 존재하는 파일이면 내용을 컨텍스트에 첨부(주입). 파일이 아니면 조용히 무시.
    private async Task AttachFileMentionsAsync(string input, CancellationToken ct)
    {
        foreach (Match m in Regex.Matches(input, @"(?<!\S)@(\S+)"))
        {
            var raw = m.Groups[1].Value.TrimEnd('.', ',', ';', ':', ')', ']', '}');
            var path = ExpandMentionPath(raw);
            if (path is null || !File.Exists(path))
            {
                continue;
            }

            try
            {
                var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                var capped = QueryEngine.CapToolOutput(text, 40000);
                _ctx.Engine.AddSystemReminder($"Attached file (via @{raw}): {path}\n---\n{capped}");
                AnsiConsole.MarkupLine($"[grey58]attached {Markup.Escape(raw)} ({text.Length} chars)[/]");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // 읽을 수 없는 파일은 건너뜀.
            }
        }
    }

    private static string? ExpandMentionPath(string p)
    {
        if (string.IsNullOrWhiteSpace(p))
        {
            return null;
        }

        if (p == "~" || p.StartsWith("~/", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            p = p == "~" ? home : Path.Combine(home, p[2..]);
        }

        try
        {
            return Path.GetFullPath(p, Directory.GetCurrentDirectory());
        }
        catch
        {
            return null;
        }
    }

    private async Task ConsumeTurnAsync(string userInput, CancellationToken ct)
    {
        // 브레인스토밍: 이 턴을 카운트하고, 한도에 도달하면 이번 턴에 플랜을 강제 마무리한다.
        if (_ctx.State.Brainstorming)
        {
            _brainstormSuggestion = null;   // 이번 턴의 새 제안으로 교체될 때까지 초기화
            _ctx.State.BrainstormTurnsLeft--;
            // 한도 도달이면 플랜 마무리, 아니면 '이번 턴 질문 하나' 넛지(컴팩션 후에도 규칙 유지).
            _ctx.Engine.AddSystemReminder(_ctx.State.BrainstormTurnsLeft <= 0
                ? Reminders.BrainstormFinalize
                : Reminders.BrainstormOneQuestion);
        }

        // 이 턴 전용 취소 토큰. Ctrl+C(시그널) 또는 ESC 로 cancel → 엔진/툴이 멈추고 프롬프트로 복귀(프로세스 유지).
        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tct = turnCts.Token;
        _activeTurnCts = turnCts;

        // ESC 워처: 턴 동안만 동작. 권한 다이얼로그/선택 위젯이 stdin 을 점유 중(IsPrompting)이면 멈춰
        // 입력 충돌(과거 desync/hang)을 피한다. 키가 실제로 있을 때만(non-blocking) 읽는다.
        using var escStop = StartEscWatcher(turnCts);

        // 상시 하단 입력바 예약(대화형 + 타입어헤드 + 터미널일 때만).
        _barWanted = _interactive && _typeAhead && !Console.IsOutputRedirected;
        ActivateBar();

        try
        {
            // 툴 호출 블록을 id로 기억해 두었다가, 실행 결과 렌더 시 Edit/Write의 입력으로 diff를 그린다.
            var pendingCalls = new Dictionary<string, ToolUseBlock>(StringComparer.Ordinal);

            // 이 턴의 토큰 사용량을 모델별로 기록하기 위한 시작 스냅샷(엔진 누적 - 시작 = 이 턴 델타).
            var startUsage = _ctx.Engine.CumulativeUsage;
            _producedOutputInTurn = false;

            await using var e = _ctx.Engine.SubmitAsync(userInput, tct).GetAsyncEnumerator(tct);
            var has = await MoveNextWithSpinnerAsync(e, L10n.Get("repl.spinner.thinking"), tct).ConfigureAwait(false);

            while (has)
            {
                switch (e.Current)
                {
                    case TextDelta:
                        has = await StreamTextRunAsync(e, tct).ConfigureAwait(false);
                        continue;
                    case ToolCallRequested t:
                        pendingCalls[t.Block.Id] = t.Block;
                        _producedOutputInTurn = true;
                        RenderToolCall(t);
                        has = await MoveNextWithSpinnerAsync(e, L10n.Get("repl.spinner.working"), tct).ConfigureAwait(false);
                        continue;
                    case ToolExecuted x:
                        pendingCalls.TryGetValue(x.ToolUseId, out var callBlock);
                        RenderToolResult(x, callBlock);
                        has = await MoveNextWithSpinnerAsync(e, L10n.Get("repl.spinner.working"), tct).ConfigureAwait(false);
                        continue;
                    case StreamNotice sn:
                        AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(sn.Text)}[/]");
                        has = await MoveNextWithSpinnerAsync(e, L10n.Get("repl.spinner.working"), tct).ConfigureAwait(false);
                        continue;
                    case TurnCompleted:
                        // 이 턴의 토큰을 현재 모델에 누적(로컬 /usage 집계).
                        var cu = _ctx.Engine.CumulativeUsage;
                        _ctx.Usage?.Record(
                            CurrentModelLabel(),
                            cu.InputTokens - startUsage.InputTokens,
                            cu.OutputTokens - startUsage.OutputTokens);
                        // 토큰 수치 대신 빈 줄 하나 — 응답과 다음 입력 프롬프트 사이 margin.
                        AnsiConsole.WriteLine();
                        has = await MoveNextWithSpinnerAsync(e, L10n.Get("repl.spinner.working"), tct).ConfigureAwait(false);
                        continue;
                    default:
                        has = await MoveNextWithSpinnerAsync(e, L10n.Get("repl.spinner.working"), tct).ConfigureAwait(false);
                        continue;
                }
            }

            // 턴이 정상 종료됐는데 화면에 아무것도 안 나왔으면(빈 응답) 사용자에게 알린다.
            if (!_producedOutputInTurn)
            {
                AnsiConsole.MarkupLine($"[grey70]{Markup.Escape(L10n.Get("repl.emptyResponse"))}[/]");
            }
        }
        catch (OperationCanceledException) when (turnCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Ctrl+C로 사용자가 중단 — 프로세스는 유지하고 다음 입력으로 복귀.
            ClearSpinnerLine();
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(L10n.Get("repl.aborted"))}[/]");
        }
        catch (OperationCanceledException)
        {
            throw; // 앱 종료 등 외부 취소는 전파
        }
        catch (Exception ex)
        {
            // 프로바이더/툴 오류로 REPL이 죽지 않도록 표시 후 계속.
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(L10n.Get("repl.error", ex.Message))}[/]");
        }
        finally
        {
            DeactivateBar();
            _barWanted = false;
            _activeTurnCts = null;
        }

        // 턴이 끝날 때마다(중단 포함) 현재 대화를 세션 파일에 자동 저장.
        try
        {
            await _ctx.Sessions.SaveAsync(_sessionId, _ctx.Engine.Messages, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // 저장 실패가 REPL을 막지 않게 한다.
        }

        // 브레인스토밍 종료 판정: 에이전트가 새 플랜을 생성(플랜 트리 변경)했거나 턴 한도 도달 시
        // 모드를 해제하고 모델을 복원한 뒤 플랜 확인/실행을 안내한다.
        if (_ctx.State.Brainstorming)
        {
            var plan = _ctx.PlanTree?.Invoke() ?? string.Empty;
            var planCreated = !string.IsNullOrWhiteSpace(plan) && plan != _ctx.State.BrainstormBasePlan;
            if (planCreated || _ctx.State.BrainstormTurnsLeft <= 0)
            {
                BrainstormCommand.End(_ctx);
                _brainstormSuggestion = null;
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"[grey70]{Markup.Escape(L10n.Get("slash.brainstorm.planReady"))}[/]");
            }
        }
    }

    // 입력 프롬프트 바로 위 상태바: 모드 (shift+tab to cycle) · 모델 · 누적 토큰.
    // 상태줄을 raw ANSI 문자열로 생성 (박스 하단 + Shift+Tab 제자리 갱신 공용).
    private string BuildStatusLine()
    {
        var (modeKey, ansi) = _ctx.State.Mode switch
        {
            AgentMode.Plan => ("repl.mode.plan", "\x1b[33m"),       // yellow
            AgentMode.AutoAct => ("repl.mode.autoAct", "\x1b[38;5;39m"), // deepskyblue
            _ => ("repl.mode.act", "\x1b[32m"),                     // green
        };

        var modeTxt = L10n.Get(modeKey);
        var toggle = L10n.Get("repl.status.toggle");
        var model = ModelStatusLabel();
        return $"{ansi}{modeTxt}\x1b[0m\x1b[38;5;249m ({toggle}) · {model}\x1b[0m";
    }

    // 상태줄 모델 표기: 난이도 티어(MOAI_MODEL_LOW/MID/HIGH)가 하나라도 설정돼 있으면
    // 티어별 모델(L/M/H), 아니면 단일 현재 모델. 미설정 티어는 기본(현재) 모델로 채운다.
    private string ModelStatusLabel()
    {
        var low = Environment.GetEnvironmentVariable("MOAI_MODEL_LOW");
        var mid = Environment.GetEnvironmentVariable("MOAI_MODEL_MID");
        var high = Environment.GetEnvironmentVariable("MOAI_MODEL_HIGH");
        if (string.IsNullOrWhiteSpace(low) && string.IsNullOrWhiteSpace(mid) && string.IsNullOrWhiteSpace(high))
        {
            return CurrentModelLabel();
        }

        var def = CurrentModelLabel();
        static string Id(string s) { var i = s.LastIndexOf('/'); return (i >= 0 ? s[(i + 1)..] : s).Trim(); }
        string T(string? v) => Id(string.IsNullOrWhiteSpace(v) ? def : v!);
        return $"L:{T(low)} M:{T(mid)} H:{T(high)}";
    }

    // act → auto-act → plan → act 순환.
    private void CycleMode()
    {
        _ctx.State.Mode = _ctx.State.Mode switch
        {
            AgentMode.Act => AgentMode.AutoAct,
            AgentMode.AutoAct => AgentMode.Plan,
            _ => AgentMode.Act,
        };

        _ctx.Engine.AddSystemReminder(_ctx.State.Mode switch
        {
            AgentMode.Plan => Reminders.PlanMode,
            AgentMode.AutoAct => Reminders.AutoActMode,
            _ => Reminders.ActMode,
        });
    }

    private static string ShortModel(string providerDesc)
    {
        var s = providerDesc ?? "";
        var at = s.IndexOf('@');
        if (at > 0)
        {
            s = s[..at];
        }

        var dot = s.LastIndexOf('·');
        if (dot >= 0)
        {
            s = s[(dot + 1)..];
        }

        s = s.Trim();
        var slash = s.LastIndexOf('/');
        return slash >= 0 ? s[(slash + 1)..].Trim() : s;
    }

    // 입력 히스토리(↑/↓ 회상)에 추가. 직전과 동일하면 중복 추가하지 않고, 크기를 제한한다.
    private void AddHistory(string line)
    {
        if (_history.Count > 0 && _history[^1] == line)
        {
            return;
        }

        _history.Add(line);
        if (_history.Count > 500)
        {
            _history.RemoveRange(0, _history.Count - 500);
        }
    }

    // Ctrl+C: 턴이 진행 중이면 그 턴만 취소하고 프로세스는 유지. 프롬프트 상태에서는 기본 동작(종료).
    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        var cts = _activeTurnCts;
        if (cts is not null && !cts.IsCancellationRequested)
        {
            e.Cancel = true;
            cts.Cancel();
        }
    }

    private static readonly string[] SpinnerFrames =
        { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };

    /// <summary>
    /// 다음 이벤트를 기다리는 동안(모델 생각/툴 실행/컴팩션 등) 하단에 스피너+경과초를 표시한다.
    /// 아무 출력도 없으면 멈춘 것처럼 보이므로 "살아있음"을 시각적으로 알린다. 이벤트가 오면 그 줄을 지운다.
    /// 비대화형(파이프/테스트)에서는 표시하지 않는다.
    /// </summary>
    private async Task<bool> MoveNextWithSpinnerAsync(
        IAsyncEnumerator<StreamEvent> e, string label, CancellationToken ct)
    {
        var pending = e.MoveNextAsync();
        if (!_interactive || pending.IsCompleted)
        {
            return await pending.ConfigureAwait(false);
        }

        var task = pending.AsTask();
        var sw = Stopwatch.StartNew();
        var frame = 0;
        while (!task.IsCompleted && !ct.IsCancellationRequested)
        {
            // 첫 한 박자(~120ms) 안에 끝나면 스피너를 그리지 않아 깜빡임을 줄인다.
            var winner = await Task.WhenAny(task, Task.Delay(120, CancellationToken.None)).ConfigureAwait(false);
            if (winner == task)
            {
                break;
            }

            // 권한/선택 프롬프트가 입력을 기다리는 동안에는 스피너를 그리지 않는다 (그 줄을 덮어쓰지 않게).
            if (ConsolePrompt.IsPrompting)
            {
                if (_barActive)
                {
                    DeactivateBar();
                }

                ClearSpinnerLine();
                continue;
            }

            if (_barWanted && !_barActive)
            {
                ActivateBar();
            }

            DrawSpinner(frame++, label, sw.Elapsed.TotalSeconds);
            DrawBar();
        }

        ClearSpinnerLine();
        return await task.ConfigureAwait(false);
    }

    // ── 상시 하단 입력바 (타입어헤드 표시) ─────────────────────────────────────────
    private static int BarHeight() { try { var h = Console.WindowHeight; return h < 1 ? 24 : h; } catch { return 24; } }
    private static int BarWidth() { try { var w = Console.WindowWidth; return w < 1 ? 80 : w; } catch { return 80; } }

    // 하단 1행을 입력바로 예약: 스크롤 영역을 1..h-1 로 설정(출력은 그 위에서 스크롤), 커서를 영역 하단으로.
    private void ActivateBar()
    {
        if (!_barWanted || _barActive)
        {
            return;
        }

        var h = BarHeight();
        if (h < 3)
        {
            return;
        }

        Console.Write($"[{h};1H\n[1;{h - 1}r[{h - 1};1H[?25l");
        _barActive = true;
        DrawBar();
    }

    // 예약된 하단 행(영역 밖)에 입력바를 그린다. 커서 저장/복원으로 출력 흐름을 방해하지 않는다.
    private void DrawBar()
    {
        if (!_barActive)
        {
            return;
        }

        var h = BarHeight();
        var w = BarWidth();
        var line = _typeAhead ? _turnInput.CurrentLine : string.Empty;
        string body;
        if (line.Length == 0)
        {
            body = $"[38;5;244m{L10n.Get("repl.typeahead.hint")}[0m";
        }
        else
        {
            var max = Math.Max(1, w - 5);
            if (line.Length > max)
            {
                line = "\u2026" + line[^(max - 1)..];
            }

            body = $"[38;5;252m{line}[0m";
        }

        var qn = _typeAhead ? _turnInput.Count : 0;
        lock (_barLock) { Console.Write($"7[{h};1H[48;5;236m[2K [38;5;245m(Q:{qn})[38;5;39m\u276f[39m {body}[7m [0m[K[0m8"); }
    }

    // 스크롤 영역 해제 + 입력바 행 지움. 턴 종료·권한창 표시 전에 호출.
    private void DeactivateBar()
    {
        if (!_barActive)
        {
            return;
        }

        var h = BarHeight();
        Console.Write($"[r[{h};1H[2K[?25h");
        _barActive = false;
    }

    private void DrawSpinner(int frame, string label, double seconds)
    {
        var spin = SpinnerFrames[frame % SpinnerFrames.Length];
        // 턴 중엔 하단 고정이 해제된 일반 터미널이라 인라인 스피너로 표시.
        // CR + 줄 전체 지우기 + dim 색으로 스피너/라벨/경과초.
        lock (_barLock) { Console.Write($"\r[2K[38;5;39m{spin} {label} ({seconds:0}s)[0m"); }
    }

    private void ClearSpinnerLine()
    {
        lock (_barLock) { Console.Write("\r[2K"); }
    }

    private async Task<bool> StreamTextRunAsync(IAsyncEnumerator<StreamEvent> e, CancellationToken ct)
    {
        var sb = new StringBuilder();

        if (!_interactive)
        {
            // 스트리밍 원문에는 추론 마커(<think>…, __THINKING_STATUS__:…)가 섞여 나오고, 마커가 델타
            // 경계를 넘나들어 조각 단위로는 지울 수 없다. 텍스트 런을 다 모은 뒤 ThinkFilter 를 적용해
            // 출력한다(예전엔 그대로 흘려보내 답변에 마커가 달라붙었다).
            var more = true;
            while (true)
            {
                if (e.Current is TextDelta d)
                {
                    sb.Append(d.Text);
                }
                else
                {
                    break;
                }

                more = await e.MoveNextAsync().ConfigureAwait(false);
                if (!more)
                {
                    break;
                }
            }

            if (_ctx.State.Brainstorming)
            {
                var (_, suggest) = ExtractSuggest(sb.ToString());   // 원문에서 제안 캡처(마커 제거는 ThinkFilter)
                if (suggest is not null) _brainstormSuggestion = suggest;
            }
            var cleanText = ThinkFilter.Strip(sb.ToString());
            if (!string.IsNullOrWhiteSpace(cleanText))
            {
                _producedOutputInTurn = true;
                AnsiConsole.Markup("[aqua]MoAI Code[/] ");
                AnsiConsole.Markup(Markup.Escape(cleanText));
                AnsiConsole.WriteLine();
            }

            return more;
        }

        // 대화형: 스트리밍 원문은 화면에 찍지 않는다. 생성 동안에는 스피너만 보여주고,
        // 텍스트를 모두 모은 뒤 마크다운을 적용한 최종 패널 하나만 깔끔하게 렌더한다.
        var hasMore = true;
        var sw = Stopwatch.StartNew();
        var frame = 0;
        long lastDrawMs = -1000;

        void Tick()
        {
            var nowMs = sw.ElapsedMilliseconds;
            if (nowMs - lastDrawMs >= 100)
            {
                DrawSpinner(frame++, L10n.Get("repl.spinner.writing"), sw.Elapsed.TotalSeconds);
                lastDrawMs = nowMs;
            }
        }

        while (e.Current is TextDelta d)
        {
            sb.Append(d.Text);
            Tick();

            var pending = e.MoveNextAsync();
            if (pending.IsCompleted)
            {
                hasMore = await pending.ConfigureAwait(false);
            }
            else
            {
                var task = pending.AsTask();
                while (!task.IsCompleted && !ct.IsCancellationRequested)
                {
                    var winner = await Task.WhenAny(task, Task.Delay(100, CancellationToken.None)).ConfigureAwait(false);
                    if (winner == task)
                    {
                        break;
                    }

                    Tick();
                }

                hasMore = await task.ConfigureAwait(false);
            }

            if (!hasMore)
            {
                break;
            }
        }

        ClearSpinnerLine();

        // 추론(<think>...</think>)을 제거하고, 실제 내용이 있을 때만 마크다운 패널을 렌더한다.
        if (_ctx.State.Brainstorming)
        {
            var (_, suggest) = ExtractSuggest(sb.ToString());   // 원문에서 제안 캡처(마커 제거는 ThinkFilter)
            if (suggest is not null) _brainstormSuggestion = suggest;
        }
        var finalText = ThinkFilter.Strip(sb.ToString());
        if (!string.IsNullOrWhiteSpace(finalText))
        {
            _producedOutputInTurn = true;
            AnsiConsole.Write(BuildRenderedPanel(finalText));
        }

        return hasMore;
    }

    // 완료 후: Markdig → Spectre 마크다운 렌더.
    private static Panel BuildRenderedPanel(string markdown)
        => new Panel(MarkdownRenderer.Render(markdown))
            .Header("[aqua]MoAI Code[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey);

    private static void RenderToolCall(ToolCallRequested t)
    {
        string d;
        if (t.Block.Name == "Bash")
        {
            // 인라인에서는 전체 명령을 풀지 않고 압축: Bash (명령어 150자…). 전체는 권한 다이얼로그에서 확인.
            var cmd = (GetStr(t.Block.Input, "command") ?? string.Empty).ReplaceLineEndings(" ").Trim();
            if (cmd.Length > 150)
            {
                cmd = cmd[..150] + "…";
            }

            d = $"Bash ({cmd})";
        }
        else
        {
            // raw JSON 대신 사람이 읽는 형태(Read /path, Grep "..." 등). 한 줄로 축약.
            d = ToolDisplay.Describe(t.Block.Name, t.Block.Input).ReplaceLineEndings(" ");
            if (d.Length > 160)
            {
                d = d[..160] + "…";
            }
        }

        AnsiConsole.MarkupLine($"[yellow]→[/] [grey70]{Markup.Escape(d)}[/]");
    }

    private static void RenderToolResult(ToolExecuted x, ToolUseBlock? call = null)
    {
        // 결과는 호출(→) 아래에 한 단계 들여써서 시각적으로 묶는다.
        const string ind = "  ";

        // 오류는 원인 파악을 위해 메시지를 보여주되 과하지 않게 절단.
        if (x.IsError)
        {
            var err = x.Output.Length > 200 ? x.Output[..200] + "…" : x.Output;
            AnsiConsole.MarkupLine(
                $"{ind}[red]✗ {Markup.Escape(x.ToolName)}[/] [grey70]{Markup.Escape(err.ReplaceLineEndings(" "))}[/]");
            return;
        }

        // Edit/Write 성공: 변경 내용을 +/- diff로 보여준다.
        if (call is not null && call.Name is "Edit" or "Write")
        {
            var path = GetStr(call.Input, "path") ?? "";
            AnsiConsole.MarkupLine($"{ind}[green]✓ {Markup.Escape(x.ToolName)}[/] [grey70]{Markup.Escape(path)}[/]");
            if (call.Name == "Edit")
            {
                DiffRenderer.Render(GetStr(call.Input, "old_string") ?? "", GetStr(call.Input, "new_string") ?? "");
            }
            else
            {
                DiffRenderer.Render("", GetStr(call.Input, "content") ?? "");
            }

            return;
        }

        // Plan/Task 트리는 잘리지 않게 전체를 색으로 렌더(진행 상황 가시화).
        if (x.ToolName is "PlanCreate" or "TaskList")
        {
            AnsiConsole.MarkupLine($"{ind}[green]✓ {Markup.Escape(x.ToolName)}[/]");
            foreach (var raw in (x.Output ?? "").Replace("\r", "").Split('\n'))
            {
                var line = raw.TrimEnd();
                if (line.Length == 0)
                {
                    continue;
                }

                var t = line.TrimStart();
                var color = t.StartsWith("[x]") || t.StartsWith("x ") ? "green"
                    : t.StartsWith("[>]") || t.StartsWith("> ") ? "aqua"
                    : t.StartsWith("[ ]") || t.StartsWith("- ") ? "grey70"
                    : "grey85";
                AnsiConsole.MarkupLine($"{ind}{ind}[{color}]{Markup.Escape(line)}[/]");
            }

            return;
        }

        // 성공 결과는 전체 덤프 대신 요약(라인 수·문자 수 + 첫 줄 미리보기).
        var output = x.Output ?? "";
        var trimmed = output.TrimEnd('\n', '\r');
        var lineCount = trimmed.Length == 0 ? 0 : trimmed.Split('\n').Length;
        var firstLine = lineCount == 0 ? "" : trimmed.Split('\n', 2)[0].Trim();
        if (firstLine.Length > 80)
        {
            firstLine = firstLine[..80] + "…";
        }

        AnsiConsole.MarkupLine(
            $"{ind}[green]✓ {Markup.Escape(x.ToolName)}[/] [grey70]({lineCount} lines · {output.Length} chars)[/]");
        if (firstLine.Length > 0)
        {
            AnsiConsole.MarkupLine($"{ind}{ind}[grey70]{Markup.Escape(firstLine)}[/]");
        }
    }

    // 진행 스피너용 간결 라벨 (예: "Read greet.py", "Bash: npm test", "Grep TODO").
    private static string ProgressLabel(ToolUseBlock call)
    {
        var name = call.Name;
        string? arg = name switch
        {
            "Read" or "Write" or "Edit" => Path.GetFileName(GetStr(call.Input, "path") ?? ""),
            "Bash" => Clip(GetStr(call.Input, "command"), 40),
            "Grep" or "Glob" => GetStr(call.Input, "pattern"),
            "Agent" => GetStr(call.Input, "description"),
            "Skill" => GetStr(call.Input, "name"),
            _ => null,
        };

        return string.IsNullOrWhiteSpace(arg) ? L10n.Get("repl.spinner.toolRunning", name) : $"{name} {arg}";
    }

    private static string? Clip(string? s, int max)
        => string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max] + "…");

    private static string? GetStr(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    // /usage 집계·상태줄에 쓰는 현재 모델 라벨. 라이브 모델(/model) 우선, 없으면 프로바이더 설명에서.
    private string CurrentModelLabel()
        => !string.IsNullOrEmpty(_ctx.Models?.CurrentModel)
            ? _ctx.Models!.CurrentModel
            : ShortModel(_ctx.ProviderDesc);

    // ESC 워처: 턴 동안 백그라운드로 ESC 를 감지해 turnCts 를 취소. IsPrompting(권한/선택 위젯이
    // stdin 점유) 중에는 절대 키를 읽지 않아 입력 충돌을 피한다. non-blocking(KeyAvailable) 폴링.
    private IDisposable StartEscWatcher(CancellationTokenSource turnCts)
    {
        if (Console.IsInputRedirected)
        {
            return new ActionDisposable(() => { });
        }

        var stop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested && !turnCts.IsCancellationRequested)
                {
                    var hit = false;
                    try
                    {
                        if (!ConsolePrompt.IsPrompting && Console.KeyAvailable)
                        {
                            var k = Console.ReadKey(intercept: true);
                            if (k.Key == ConsoleKey.Escape)
                            {
                                hit = true;
                            }
                            else if (_typeAhead)
                            {
                                // 타입어헤드: 다른 키는 버리지 않고 큐에 모은다(턴 종료 후 순차 제출).
                                _turnInput.Feed(k);
                                DrawBar();   // 키 입력 즉시 하단 바 갱신
                            }
                        }
                    }
                    catch
                    {
                        // KeyAvailable/ReadKey 일시 오류 무시
                    }

                    if (hit)
                    {
                        turnCts.Cancel();
                        break;
                    }

                    try
                    {
                        await Task.Delay(40, stop.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            catch
            {
                // 워처 오류로 앱이 죽지 않게
            }
        });

        return new ActionDisposable(() =>
        {
            try
            {
                stop.Cancel();
            }
            catch
            {
                // ignore
            }
        });
    }

    private sealed class ActionDisposable : IDisposable
    {
        private readonly Action _onDispose;
        public ActionDisposable(Action onDispose) => _onDispose = onDispose;
        public void Dispose() => _onDispose();
    }
}
