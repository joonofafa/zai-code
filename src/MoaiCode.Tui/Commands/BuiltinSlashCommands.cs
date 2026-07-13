using MoaiCode.Core.Tools;
using MoaiCode.Persistence;

namespace MoaiCode.Tui.Commands;

internal sealed class ExitCommand : ISlashCommand
{
    public string Name => "exit";
    public string Description => "REPL 종료";
    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
        => Task.FromResult(new SlashResult("", Quit: true));
}

internal sealed class ClearCommand : ISlashCommand
{
    public string Name => "clear";
    public string Description => "대화 초기화";
    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        ctx.Engine.Reset();
        return Task.FromResult(new SlashResult("(대화 초기화됨)"));
    }
}

internal sealed class ToolsCommand : ISlashCommand
{
    public string Name => "tools";
    public string Description => "사용 가능한 툴 목록";
    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
        => Task.FromResult(new SlashResult($"tools: {string.Join(", ", ctx.ToolNames)}"));
}

internal sealed class ModelCommand : ISlashCommand
{
    public string Name => "model";
    public string Description => "모델 변경 (목록에서 선택)";

    // 로그인 직후 모델 선택과 동일한 화살표 선택 화면을 띄워 라이브 세션의 모델을 교체한다.
    public async Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        var mc = ctx.Models;
        if (mc is null)
        {
            return new SlashResult($"이 프로바이더는 모델 전환을 지원하지 않습니다. ({ctx.ProviderDesc})");
        }

        Spectre.Console.AnsiConsole.MarkupLine("[grey70]모델 목록 조회 중…[/]");
        var models = await mc.ListModelsAsync(ct).ConfigureAwait(false);
        if (models.Count == 0)
        {
            return new SlashResult("모델 목록을 가져오지 못했습니다 (로그인/네트워크 확인).");
        }

        // 현재 모델을 기본 선택으로.
        var list = models.ToList();
        var defIdx = list.FindIndex(m => string.Equals(m, mc.CurrentModel, StringComparison.OrdinalIgnoreCase));
        if (defIdx < 0)
        {
            defIdx = 0;
        }

        var pick = SelectList.Prompt("사용할 모델을 선택하세요:", models, defIdx);
        if (pick < 0)
        {
            return new SlashResult("변경 없음");
        }

        var chosen = models[pick];
        mc.CurrentModel = chosen;          // 라이브 반영 (QueryEngine/서브에이전트/컴팩션 공유 인스턴스)
        ctx.PersistModel?.Invoke(chosen);  // settings.json + env 저장 (다음 실행에도 유지)
        return new SlashResult($"모델 변경됨: {chosen}");
    }
}

internal sealed class SkillsCommand : ISlashCommand
{
    public string Name => "skills";
    public string Description => "로드된 스킬 목록";
    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
        => Task.FromResult(new SlashResult(
            ctx.SkillNames.Count == 0 ? "skills: (없음)" : $"skills: {string.Join(", ", ctx.SkillNames)}"));
}

internal sealed class McpCommand : ISlashCommand
{
    public string Name => "mcp";
    public string Description => "연결된 MCP 서버";
    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
        => Task.FromResult(new SlashResult(
            ctx.McpServers.Count == 0 ? "mcp: (없음)" : $"mcp servers: {string.Join(", ", ctx.McpServers)}"));
}

internal sealed class CostCommand : ISlashCommand
{
    public string Name => "cost";
    public string Description => "세션 누적 토큰 사용량";
    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        var u = ctx.Engine.CumulativeUsage;
        return Task.FromResult(new SlashResult(
            $"usage: in={u.InputTokens} out={u.OutputTokens} cacheRead={u.CacheReadTokens}"));
    }
}

// /permissions: 영속 allow/deny 규칙 조회·편집 (Claude Code permissions.allow/deny 대응).
internal sealed class PermissionsCommand : ISlashCommand
{
    public string Name => "permissions";
    public string Description => "권한 규칙 조회·추가·삭제 (allow/deny · settings.json 영속)";

    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        var rules = ctx.Rules;
        if (rules is null)
        {
            return Task.FromResult(new SlashResult("권한 규칙 저장소를 사용할 수 없습니다."));
        }

        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        var rest = string.Join(' ', args.Skip(1)).Trim();

        switch (sub)
        {
            case "allow" when rest.Length > 0:
                rules.AddAllow(rest);
                return Task.FromResult(new SlashResult($"allow 추가: {rest}"));
            case "deny" when rest.Length > 0:
                rules.AddDeny(rest);
                return Task.FromResult(new SlashResult($"deny 추가: {rest}"));
            case "remove" when rest.Length > 0:
                var ok = rules.Remove(rest);
                return Task.FromResult(new SlashResult(ok ? $"삭제: {rest}" : $"해당 규칙 없음: {rest}"));
            case "allow":
            case "deny":
            case "remove":
                return Task.FromResult(new SlashResult(
                    "사용법: /permissions allow <패턴> · deny <패턴> · remove <패턴>\n"
                    + "예: /permissions allow Bash(ssh moai-ec2)"));
            default:
                Render(rules);
                return Task.FromResult(new SlashResult(string.Empty));
        }
    }

    private static void Render(IPermissionRuleStore rules)
    {
        Spectre.Console.AnsiConsole.WriteLine();
        Spectre.Console.AnsiConsole.MarkupLine("[aqua]권한 규칙[/] [grey70]· settings.json 에 저장됨[/]");
        Spectre.Console.AnsiConsole.MarkupLine("[green]allow[/] " + (rules.Allow.Count == 0 ? "[grey58](없음)[/]" : ""));
        foreach (var r in rules.Allow)
        {
            Spectre.Console.AnsiConsole.MarkupLine($"  [grey85]{Spectre.Console.Markup.Escape(r)}[/]");
        }

        Spectre.Console.AnsiConsole.MarkupLine("[red]deny[/] " + (rules.Deny.Count == 0 ? "[grey58](없음)[/]" : ""));
        foreach (var r in rules.Deny)
        {
            Spectre.Console.AnsiConsole.MarkupLine($"  [grey85]{Spectre.Console.Markup.Escape(r)}[/]");
        }

        Spectre.Console.AnsiConsole.MarkupLine(
            "[grey58]추가: /permissions allow Bash(git status) · 삭제: /permissions remove <패턴>[/]");
    }
}

// /usage: 로그인 계정·시간 + 모델별 로컬 토큰 사용량. Spectre 로 직접 렌더(색/정렬)하고 빈 결과 반환.
internal sealed class UsageCommand : ISlashCommand
{
    public string Name => "usage";
    public string Description => "로그인 계정·시간·모델별 토큰 사용량(로컬)";

    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        var ac = ctx.Account;
        var email = string.IsNullOrWhiteSpace(ac?.Email) ? "(알 수 없음)" : ac!.Email!;
        var host = string.IsNullOrWhiteSpace(ac?.Host) ? "(미설정)" : ac!.Host!;
        var org = string.IsNullOrWhiteSpace(ac?.OrgName) ? null : ac!.OrgName!;

        Spectre.Console.AnsiConsole.WriteLine();
        Spectre.Console.AnsiConsole.MarkupLine(
            $"[aqua]계정[/] [grey85]{Spectre.Console.Markup.Escape(email)}[/] [grey70]· {Spectre.Console.Markup.Escape(host)}[/]");
        if (org is not null)
        {
            Spectre.Console.AnsiConsole.MarkupLine(
                $"[grey70]조직: [/][grey85]{Spectre.Console.Markup.Escape(org)}[/]");
        }
        Spectre.Console.AnsiConsole.MarkupLine(
            $"[grey70]로그인: {Spectre.Console.Markup.Escape(FormatTime(ac?.LoginAt))} · 현재: {DateTimeOffset.Now:yyyy-MM-dd HH:mm}[/]");

        var usage = ctx.Usage;
        var rows = usage?.All() ?? System.Array.Empty<ModelUsage>();
        var since = usage is not null ? $" · {usage.Since:yyyy-MM-dd}부터" : "";

        Spectre.Console.AnsiConsole.WriteLine();
        Spectre.Console.AnsiConsole.MarkupLine($"[grey70]모델별 토큰 사용량 (로컬 기준{since})[/]");

        if (rows.Count == 0)
        {
            Spectre.Console.AnsiConsole.MarkupLine("[grey70]  (아직 기록 없음 — 대화 후 표시됩니다)[/]");
            return Task.FromResult(new SlashResult(""));
        }

        long ti = 0, to = 0, tt = 0;
        var nameW = System.Math.Max(10, rows.Max(r => r.Model.Length));
        foreach (var r in rows)
        {
            ti += r.InputTokens;
            to += r.OutputTokens;
            tt += r.Turns;
            Spectre.Console.AnsiConsole.MarkupLine(
                $"[grey85]  {Spectre.Console.Markup.Escape(r.Model.PadRight(nameW))}[/] " +
                $"[grey70]in[/] [white]{r.InputTokens,11:N0}[/]  " +
                $"[grey70]out[/] [white]{r.OutputTokens,10:N0}[/]  " +
                $"[grey70]turns {r.Turns}[/]");
        }

        Spectre.Console.AnsiConsole.MarkupLine(
            $"[grey70]  {Spectre.Console.Markup.Escape("합계".PadRight(nameW))}[/] " +
            $"[grey70]in[/] [white]{ti,11:N0}[/]  " +
            $"[grey70]out[/] [white]{to,10:N0}[/]  " +
            $"[grey70]turns {tt}[/]");

        return Task.FromResult(new SlashResult(""));
    }

    private static string FormatTime(string? iso)
        => !string.IsNullOrWhiteSpace(iso) && DateTimeOffset.TryParse(iso, out var t)
            ? t.ToString("yyyy-MM-dd HH:mm")
            : "(알 수 없음)";
}

internal sealed class PlanModeCommand : ISlashCommand
{
    public string Name => "plan";
    public string Description => "Plan 모드로 전환(읽기 전용)";
    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        ctx.State.Mode = AgentMode.Plan;
        ctx.Engine.AddSystemReminder(MoaiCode.Core.Agent.Prompts.Reminders.PlanMode);
        return Task.FromResult(new SlashResult("mode: plan (읽기 전용; /act 또는 Shift+Tab 로 전환)"));
    }
}

internal sealed class ActModeCommand : ISlashCommand
{
    public string Name => "act";
    public string Description => "Act 모드로 전환(파일 수정/명령 허용)";
    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        ctx.State.Mode = AgentMode.Act;
        ctx.Engine.AddSystemReminder(MoaiCode.Core.Agent.Prompts.Reminders.ActMode);
        return Task.FromResult(new SlashResult("mode: act"));
    }
}

internal sealed class CheckpointCreateCommand : ISlashCommand
{
    public string Name => "checkpoint";
    public string Description => "현재 workspace checkpoint 생성";
    public async Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        var subject = args.Length == 0 ? "manual checkpoint" : string.Join(' ', args);
        var id = await ctx.Checkpoints.CreateAsync(subject, ct).ConfigureAwait(false);
        return new SlashResult($"checkpoint: {id}");
    }
}

internal sealed class CheckpointsCommand : ISlashCommand
{
    public string Name => "checkpoints";
    public string Description => "최근 checkpoint 목록";
    public async Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        var list = await ctx.Checkpoints.ListAsync(ct).ConfigureAwait(false);
        if (list.Count == 0)
        {
            return new SlashResult("checkpoints: (없음)");
        }

        var lines = list.Select(c => $"{c.Id}\t{c.CreatedAt:yyyy-MM-dd HH:mm:ss}\t{c.Subject}");
        return new SlashResult(string.Join("\n", lines));
    }
}

internal sealed class CheckpointDiffCommand : ISlashCommand
{
    public string Name => "checkpoint-diff";
    public string Description => "checkpoint 대비 현재 변경 요약: /checkpoint-diff [id]";
    public async Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        var diff = await ctx.Checkpoints.DiffAsync(args.FirstOrDefault(), ct).ConfigureAwait(false);
        return new SlashResult(diff);
    }
}

internal sealed class RestoreCommand : ISlashCommand
{
    public string Name => "restore";
    public string Description => "checkpoint 파일 상태 복원: /restore <id>";
    public async Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            return new SlashResult("사용법: /restore <checkpoint-id>");
        }

        await ctx.Checkpoints.RestoreAsync(args[0], ct).ConfigureAwait(false);
        return new SlashResult($"restored checkpoint: {args[0]}");
    }
}

internal sealed class SessionsCommand : ISlashCommand
{
    public string Name => "sessions";
    public string Description => "저장된 세션 선택·복원 (↑/↓ 화살표)";
    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
        => SessionPicker.RunAsync(ctx, ct);
}

internal sealed class SaveCommand : ISlashCommand
{
    public string Name => "save";
    public string Description => "현재 세션 저장: /save <name>";
    public async Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            return new SlashResult("사용법: /save <name>");
        }

        await ctx.Sessions.SaveAsync(args[0], ctx.Engine.Messages, ct).ConfigureAwait(false);
        return new SlashResult($"저장됨: {args[0]} ({ctx.Engine.Messages.Count} messages)");
    }
}

internal sealed class ResumeCommand : ISlashCommand
{
    public string Name => "resume";
    public string Description => "세션 복원: /resume <번호> 또는 /resume <id> (목록은 /sessions)";
    public async Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        // 인자 없으면 화살표 picker (= /sessions).
        if (args.Length == 0)
        {
            return await SessionPicker.RunAsync(ctx, ct).ConfigureAwait(false);
        }

        // 번호(1,2,3…)면 최근순 목록에서 해당 항목을 고른다.
        var id = args[0];
        if (int.TryParse(id, out var n))
        {
            var infos = await ctx.Sessions.ListInfosAsync(ct).ConfigureAwait(false);
            if (n < 1 || n > infos.Count)
            {
                return new SlashResult($"잘못된 번호: {n} (1~{infos.Count})");
            }

            id = infos[n - 1].Id;
        }

        return await SessionPicker.ResumeAsync(ctx, id, ct).ConfigureAwait(false);
    }
}

internal sealed class HistoryCommand : ISlashCommand
{
    public string Name => "history";
    public string Description => "최근 입력 히스토리";
    public async Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        var recent = await ctx.History.RecentAsync(20, ct).ConfigureAwait(false);
        return new SlashResult(
            recent.Count == 0 ? "history: (없음)" : string.Join("\n", recent));
    }
}

internal sealed class InitCommand : ISlashCommand
{
    public string Name => "init";
    public string Description => "프로젝트 분석 후 CLAUDE.md 생성/갱신";
    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
        => Task.FromResult(new SlashResult("", SubmitPrompt:
            "Analyze this codebase and create a CLAUDE.md file in the working directory that gives future " +
            "agents the context they need to be productive here. If a CLAUDE.md already exists, improve it " +
            "rather than replacing it.\n\n" +
            "Include: (1) what the project is and its high-level architecture; (2) the key build, test, " +
            "lint, and run commands (read package.json / Makefile / pyproject.toml / *.csproj as relevant); " +
            "(3) important conventions, patterns, and gotchas; (4) the layout of the main directories.\n\n" +
            "Keep it concise and high-signal — it is loaded into every session's context. Use Read, Glob, " +
            "and Grep to investigate first, then Write to create CLAUDE.md."));
}

internal sealed class ReviewCommand : ISlashCommand
{
    public string Name => "review";
    public string Description => "PR 코드 리뷰: /review [PR번호]";
    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        var prArg = args.Length > 0 ? args[0] : "";
        return Task.FromResult(new SlashResult("", SubmitPrompt:
            "You are an expert code reviewer. Follow these steps:\n" +
            "1. If no PR number is provided, run `gh pr list` to show open PRs.\n" +
            "2. If a PR number is provided, run `gh pr view <number>` to get details.\n" +
            "3. Run `gh pr diff <number>` to get the diff.\n" +
            "4. Provide a thorough review: overview of what the PR does, code quality/style analysis, " +
            "specific improvement suggestions, and any potential issues or risks.\n\n" +
            "Focus on correctness, project conventions, performance, test coverage, and security. Keep it " +
            "concise but thorough, with clear sections and bullet points.\n\n" +
            $"PR number: {prArg}"));
    }
}

internal sealed class SecurityReviewCommand : ISlashCommand
{
    public string Name => "security-review";
    public string Description => "변경 코드 보안 리뷰 (OWASP, 고신뢰만)";
    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
        => Task.FromResult(new SlashResult("", SubmitPrompt:
            "Perform a security review of the pending changes on this branch.\n" +
            "1. Run `git diff` (and `git diff --staged`) to see the changes; if empty, review recent commits.\n" +
            "2. Analyze ONLY the changed code for security vulnerabilities — OWASP Top 10: injection (SQL/command/" +
            "XSS), auth/authz flaws, secrets/credentials in code, path traversal, SSRF, insecure deserialization, " +
            "weak crypto, missing input validation at trust boundaries.\n" +
            "3. Report ONLY high-confidence findings (you are >80% sure it is a real, exploitable issue). For each: " +
            "file:line, the vulnerability, a concrete exploit scenario, and the fix. Skip style/theoretical nits.\n" +
            "4. If you find no high-confidence issues, say so plainly. Do not invent problems."));
}

internal sealed class BugHunterCommand : ISlashCommand
{
    public string Name => "bughunter";
    public string Description => "다단계 버그 헌트 (탐색→검증→수정 제안)";
    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        var scope = args.Length > 0 ? string.Join(' ', args) : "the pending changes on this branch (git diff)";
        return Task.FromResult(new SlashResult("", SubmitPrompt:
            $"Hunt for real bugs in {scope}. Work in stages and be skeptical:\n" +
            "1. MAP: read the relevant code and list the areas/behaviors at risk.\n" +
            "2. HUNT: look for correctness bugs — off-by-one, null/empty handling, wrong conditions, race " +
            "conditions, resource leaks, incorrect error handling, broken edge cases, type/encoding mistakes.\n" +
            "3. VERIFY (skeptic pass): for each candidate, try to DISPROVE it by re-reading the code and tracing " +
            "the actual execution path. Discard anything you can't confirm is a real bug.\n" +
            "4. REPORT only confirmed bugs: file:line, what's wrong, how it manifests, and a minimal fix. " +
            "Do NOT apply changes unless I ask — propose first. If nothing is confirmed, say so."));
    }
}

internal sealed class SimplifyCommand : ISlashCommand
{
    public string Name => "simplify";
    public string Description => "변경 코드 단순화/재사용/효율 리뷰 후 적용";
    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
        => Task.FromResult(new SlashResult("", SubmitPrompt:
            "Review the changed code (git diff) for quality cleanups — NOT bug hunting. Look for three things:\n" +
            "1. REUSE: duplicated logic or reinvented helpers that an existing function/utility already covers.\n" +
            "2. SIMPLIFICATION: needless complexity, dead code, over-abstraction, redundant conditionals, " +
            "speculative generality that the task didn't require.\n" +
            "3. EFFICIENCY: obvious wasteful work (repeated I/O, N+1, unnecessary allocations in hot paths).\n\n" +
            "Apply the high-confidence cleanups directly with Edit, matching the surrounding code style. Keep " +
            "behavior identical — do not change functionality. Skip subjective/risky rewrites. Summarize what " +
            "you changed and why."));
}

internal sealed class HelpCommand : ISlashCommand
{
    private readonly IReadOnlyList<ISlashCommand> _all;
    public HelpCommand(IReadOnlyList<ISlashCommand> all) => _all = all;
    public string Name => "help";
    public string Description => "명령 도움말";
    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        var lines = _all.Select(c => $"/{c.Name} — {c.Description}");
        return Task.FromResult(new SlashResult(string.Join("\n", lines)));
    }
}
