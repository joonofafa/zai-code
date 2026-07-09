using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MoaiCode.Core.Agent;
using MoaiCode.Core.Agent.Prompts;
using MoaiCode.Core.Messages;
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
        AnsiConsole.MarkupLine("[grey70]MoAI Code — Enterprise Coding Agent[/]");
        AnsiConsole.MarkupLine("[grey70]도움말: /help · 진행 중 ESC 또는 Ctrl+C=중단 · ↑/↓ 히스토리 · Tab 자동완성[/]");
        AnsiConsole.MarkupLine($"[grey70]session: {Markup.Escape(_sessionId)} (자동 저장 · /resume {Markup.Escape(_sessionId)} 로 복원)[/]");
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
            if (_dock is not null)
            {
                // 하단 고정: 상태줄+입력창은 화면 맨 아래, 출력은 위 영역에서 스크롤.
                input = _dock.ReadLine(_history, _slashNames,
                    () => { CycleMode(); return BuildStatusLine(); });
            }
            else if (_useRawEditor)
            {
                Func<string> cycle = () => { CycleMode(); return BuildStatusLine(); };
                // 상태줄을 프롬프트 위에 출력하는 단순 모드 (wrap 중복 없음).
                input = LineEditor.ReadLine(_history, _slashNames, cycle, BuildStatusLine);
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

            var trimmed = input.Trim();
            if (trimmed.Length > 0)
            {
                AddHistory(trimmed); // 히스토리(↑)에는 접힌 표식 그대로 — 다시 불러도 확장된다.
            }

            // 붙여넣기 표식을 원문으로 되돌린 뒤 모델/세션에 전달한다.
            var expanded = PasteStore.Expand(trimmed);

            if (expanded.StartsWith('/'))
            {
                quit = await HandleCommandAsync(expanded, ct).ConfigureAwait(false);
                continue;
            }

            if (string.IsNullOrWhiteSpace(expanded))
            {
                continue;
            }

            await _ctx.History.AppendAsync(expanded, ct).ConfigureAwait(false);
            await ConsumeTurnAsync(expanded, ct).ConfigureAwait(false);
        }
    }

    private async Task<bool> HandleCommandAsync(string line, CancellationToken ct)
    {
        var parts = line[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var name = parts.Length > 0 ? parts[0] : "";
        var args = parts.Skip(1).ToArray();

        if (!_slash.TryGet(name, out var cmd))
        {
            AnsiConsole.MarkupLine($"[red]알 수 없는 명령: /{Markup.Escape(name)}[/]");
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
            AnsiConsole.MarkupLine($"[red]/{Markup.Escape(name)} 실행 오류: {Markup.Escape(ex.Message)}[/]");
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

    private async Task ConsumeTurnAsync(string userInput, CancellationToken ct)
    {
        // 이 턴 전용 취소 토큰. Ctrl+C(시그널) 또는 ESC 로 cancel → 엔진/툴이 멈추고 프롬프트로 복귀(프로세스 유지).
        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tct = turnCts.Token;
        _activeTurnCts = turnCts;

        // ESC 워처: 턴 동안만 동작. 권한 다이얼로그/선택 위젯이 stdin 을 점유 중(IsPrompting)이면 멈춰
        // 입력 충돌(과거 desync/hang)을 피한다. 키가 실제로 있을 때만(non-blocking) 읽는다.
        using var escStop = StartEscWatcher(turnCts);

        try
        {
            // 툴 호출 블록을 id로 기억해 두었다가, 실행 결과 렌더 시 Edit/Write의 입력으로 diff를 그린다.
            var pendingCalls = new Dictionary<string, ToolUseBlock>(StringComparer.Ordinal);

            // 이 턴의 토큰 사용량을 모델별로 기록하기 위한 시작 스냅샷(엔진 누적 - 시작 = 이 턴 델타).
            var startUsage = _ctx.Engine.CumulativeUsage;
            _producedOutputInTurn = false;

            await using var e = _ctx.Engine.SubmitAsync(userInput, tct).GetAsyncEnumerator(tct);
            var has = await MoveNextWithSpinnerAsync(e, "생각 중", tct).ConfigureAwait(false);

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
                        has = await MoveNextWithSpinnerAsync(e, "처리 중", tct).ConfigureAwait(false);
                        continue;
                    case ToolExecuted x:
                        pendingCalls.TryGetValue(x.ToolUseId, out var callBlock);
                        RenderToolResult(x, callBlock);
                        has = await MoveNextWithSpinnerAsync(e, "처리 중", tct).ConfigureAwait(false);
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
                        has = await MoveNextWithSpinnerAsync(e, "처리 중", tct).ConfigureAwait(false);
                        continue;
                    default:
                        has = await MoveNextWithSpinnerAsync(e, "처리 중", tct).ConfigureAwait(false);
                        continue;
                }
            }

            // 턴이 정상 종료됐는데 화면에 아무것도 안 나왔으면(빈 응답) 사용자에게 알린다.
            if (!_producedOutputInTurn)
            {
                AnsiConsole.MarkupLine("[grey70](빈 응답 — 모델이 콘텐츠를 반환하지 않았습니다. /model 로 다른 모델을 선택해 보세요.)[/]");
            }
        }
        catch (OperationCanceledException) when (turnCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Ctrl+C로 사용자가 중단 — 프로세스는 유지하고 다음 입력으로 복귀.
            ClearSpinnerLine();
            AnsiConsole.MarkupLine("[yellow]⊘ 중단됨[/]");
        }
        catch (OperationCanceledException)
        {
            throw; // 앱 종료 등 외부 취소는 전파
        }
        catch (Exception ex)
        {
            // 프로바이더/툴 오류로 REPL이 죽지 않도록 표시 후 계속.
            AnsiConsole.MarkupLine($"[red]오류: {Markup.Escape(ex.Message)}[/]");
        }
        finally
        {
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
    }

    // 입력 프롬프트 바로 위 상태바: 모드 (shift+tab to cycle) · 모델 · 누적 토큰.
    // 상태줄을 raw ANSI 문자열로 생성 (박스 하단 + Shift+Tab 제자리 갱신 공용).
    private string BuildStatusLine()
    {
        var (modeTxt, ansi) = _ctx.State.Mode switch
        {
            AgentMode.Plan => ("plan mode", "\x1b[33m"),       // yellow
            AgentMode.AutoAct => ("auto-act mode", "\x1b[38;5;39m"), // deepskyblue
            _ => ("act mode", "\x1b[32m"),                     // green
        };

        var model = CurrentModelLabel();
        return $"{ansi}{modeTxt}\x1b[0m\x1b[38;5;249m (shift+tab to cycle) · {model}\x1b[0m";
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
                ClearSpinnerLine();
                continue;
            }

            DrawSpinner(frame++, label, sw.Elapsed.TotalSeconds);
        }

        ClearSpinnerLine();
        return await task.ConfigureAwait(false);
    }

    private void DrawSpinner(int frame, string label, double seconds)
    {
        var spin = SpinnerFrames[frame % SpinnerFrames.Length];
        // 턴 중엔 하단 고정이 해제된 일반 터미널이라 인라인 스피너로 표시.
        // CR + 줄 전체 지우기 + dim 색으로 스피너/라벨/경과초.
        Console.Write($"\r[2K[38;5;39m{spin} {label} ({seconds:0}s)[0m");
    }

    private void ClearSpinnerLine()
    {
        Console.Write("\r[2K");
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
                DrawSpinner(frame++, "응답 작성 중", sw.Elapsed.TotalSeconds);
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

        return string.IsNullOrWhiteSpace(arg) ? $"{name} 실행 중" : $"{name} {arg}";
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
    private static IDisposable StartEscWatcher(CancellationTokenSource turnCts)
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
                            hit = k.Key == ConsoleKey.Escape && !ConsolePrompt.IsPrompting;
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
