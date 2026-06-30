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
    public string Description => "현재 프로바이더/모델";
    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
        => Task.FromResult(new SlashResult($"provider: {ctx.ProviderDesc}"));
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
