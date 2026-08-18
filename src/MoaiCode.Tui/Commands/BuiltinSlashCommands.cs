using MoaiCode.Core.Tools;
using MoaiCode.Localization;
using MoaiCode.Persistence;

namespace MoaiCode.Tui.Commands;

internal sealed class ExitCommand : ISlashCommand
{
    public string Name => "exit";
    public string Description => L10n.Get("slash.exit.description");
    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
        => Task.FromResult(new SlashResult("", Quit: true));
}

internal sealed class ClearCommand : ISlashCommand
{
    public string Name => "clear";
    public string Description => L10n.Get("slash.clear.description");
    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        ctx.Engine.Reset();
        return Task.FromResult(new SlashResult(L10n.Get("slash.clear.done")));
    }
}

internal sealed class ToolsCommand : ISlashCommand
{
    public string Name => "tools";
    public string Description => L10n.Get("slash.tools.description");
    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
        => Task.FromResult(new SlashResult($"tools: {string.Join(", ", ctx.ToolNames)}"));
}

internal sealed class ModelCommand : ISlashCommand
{
    public string Name => "model";
    public string Description => L10n.Get("slash.model.description");

    // 라이브 세션의 모델을 선택한다. 공용 ModelPicker(로그인 화면과 공유): "모델별 업무 분할(Y/n)"
    // → 예=하/중/상 티어, 아니오=단일 모델. 저장 방식만 콜백으로 주입한다.
    public async Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        var mc = ctx.Models;
        if (mc is null)
        {
            return new SlashResult(L10n.Get("slash.model.unsupported", ctx.ProviderDesc));
        }

        Spectre.Console.AnsiConsole.MarkupLine($"[grey70]{Spectre.Console.Markup.Escape(L10n.Get("slash.model.fetching"))}[/]");
        var models = await mc.ListModelsAsync(ct).ConfigureAwait(false);
        if (models.Count == 0)
        {
            return new SlashResult(L10n.Get("slash.model.fetchFailed"));
        }

        var summary = ModelPicker.Run(
            models,
            mc.CurrentModel,
            chosen => { mc.CurrentModel = chosen; ctx.PersistModel?.Invoke(chosen); }); // 라이브 반영 + settings/env 영속
        return new SlashResult(summary);
    }
}

internal sealed class EffortCommand : ISlashCommand
{
    public string Name => "effort";
    public string Description => L10n.Get("slash.effort.description");

    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        var current = (Environment.GetEnvironmentVariable("MOAI_REASONING_EFFORT")
                       ?? Environment.GetEnvironmentVariable("OPENAI_REASONING_EFFORT")
                       ?? Environment.GetEnvironmentVariable("MOAI_EFFORT")
                       ?? ctx.ReasoningEffort)
            ?.Trim().ToLowerInvariant();

        if (args.Length == 0)
        {
            return Task.FromResult(new SlashResult($"effort: {current ?? "(unset)"}"));
        }

        var value = args[0].Trim().ToLowerInvariant();
        if (value is "select")
        {
            if (Console.IsInputRedirected)
            {
                return Task.FromResult(new SlashResult(L10n.Get("slash.effort.noSelectNonInteractive")));
            }

            var persist = ctx.PersistEffort;
            if (persist is null)
            {
                return Task.FromResult(new SlashResult(L10n.Get("slash.effort.unsupported")));
            }

            var options = new[] { "low", "medium", "high" };
            var defaultIndex = Array.IndexOf(options, current);
            if (defaultIndex < 0)
            {
                defaultIndex = 1;
            }

            var pick = SelectList.Prompt(L10n.Get("slash.effort.pickTitle"), options, defaultIndex);
            if (pick < 0)
            {
                return Task.FromResult(new SlashResult(L10n.Get("common.unchanged")));
            }

            var chosen = options[pick];
            persist(chosen);
            return Task.FromResult(new SlashResult(L10n.Get("slash.effort.changed", chosen)));
        }

        if (value is not ("low" or "medium" or "high"))
        {
            return Task.FromResult(new SlashResult(L10n.Get("slash.effort.usage")));
        }

        var persistSetter = ctx.PersistEffort;
        if (persistSetter is null)
        {
            return Task.FromResult(new SlashResult(L10n.Get("slash.effort.unsupported")));
        }

        persistSetter(value);
        return Task.FromResult(new SlashResult(L10n.Get("slash.effort.changed", value)));
    }
}

internal sealed class LanguageCommand : ISlashCommand
{
    public string Name => "language";
    public string Description => L10n.Get("slash.language.description");

    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            var current = L10n.CurrentLanguage;
            return Task.FromResult(new SlashResult(
                L10n.Get("slash.language.current", current, L10n.GetLanguageDisplayName(current))));
        }

        if (args.Length != 1)
        {
            return Task.FromResult(new SlashResult(L10n.Get("slash.language.usage")));
        }

        if (string.Equals(args[0], "list", StringComparison.OrdinalIgnoreCase))
        {
            var available = string.Join(", ", L10n.SupportedLanguages.Select(x => $"{x.Code} ({x.DisplayName})"));
            return Task.FromResult(new SlashResult(L10n.Get("slash.language.available", available)));
        }

        var language = L10n.NormalizeLanguage(args[0]);
        if (language is null)
        {
            return Task.FromResult(new SlashResult(
                L10n.Get("slash.language.unsupported", args[0]) + Environment.NewLine +
                L10n.Get("slash.language.usage")));
        }

        L10n.SetLanguage(language);
        ctx.PersistLanguage?.Invoke(language);
        return Task.FromResult(new SlashResult(
            L10n.Get("slash.language.changed", language, L10n.GetLanguageDisplayName(language))));
    }
}

internal sealed class SkillsCommand : ISlashCommand
{
    public string Name => "skills";
    public string Description => L10n.Get("slash.skills.description");

    public async Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        // /skills — 대화형이면 체크박스 피커로 활성/비활성 토글, 아니면 목록만 출력.
        if (ctx.GetSkillChoices is not null && ctx.SetDisabledSkills is not null && !Console.IsInputRedirected)
        {
            var choices = ctx.GetSkillChoices();
            if (choices.Count == 0)
            {
                return new SlashResult(L10n.Get("slash.skills.none"));
            }

            var disabled = SkillPicker.Run(L10n.Get("slash.skills.pickerTitle"), choices);
            if (disabled is null)
            {
                return new SlashResult(L10n.Get("slash.skills.cancelled"));
            }

            return new SlashResult(ctx.SetDisabledSkills(disabled));
        }

        return new SlashResult(ctx.SkillNames.Count == 0
            ? L10n.Get("slash.skills.none")
            : L10n.Get("slash.skills.list", string.Join(", ", ctx.SkillNames)));
    }
}

// /install·/uninstall: Windows 셸 통합(PATH + 탐색기 우클릭 "Zai Code로 열기").
internal sealed class InstallCommand : ISlashCommand
{
    public string Name => "install";
    public string Description => L10n.Get("slash.install.description");

    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
        => Task.FromResult(new SlashResult(
            ctx.InstallIntegration is null ? L10n.Get("slash.install.unavailable") : ctx.InstallIntegration()));
}

internal sealed class UninstallCommand : ISlashCommand
{
    public string Name => "uninstall";
    public string Description => L10n.Get("slash.uninstall.description");

    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
        => Task.FromResult(new SlashResult(
            ctx.UninstallIntegration is null ? L10n.Get("slash.install.unavailable") : ctx.UninstallIntegration()));
}

internal sealed class McpCommand : ISlashCommand
{
    public string Name => "mcp";
    public string Description => L10n.Get("slash.mcp.description");
    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
        => Task.FromResult(new SlashResult(
            ctx.McpServers.Count == 0
                ? L10n.Get("slash.mcp.none")
                : L10n.Get("slash.mcp.list", string.Join(", ", ctx.McpServers))));
}

// /plan: 현재 실행 계획(Phase 트리)을 표시.
internal sealed class PlanCommand : ISlashCommand
{
    public string Name => "plan";
    public string Description => L10n.Get("slash.plan.treeDescription");
    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        var tree = ctx.PlanTree?.Invoke();
        return Task.FromResult(new SlashResult(string.IsNullOrWhiteSpace(tree) ? L10n.Get("slash.plan.none") : tree));
    }
}

// /brainstorming [화두]: 화두를 Q&A로 구체화한 뒤 에이전트가 플랜을 생성한다. 최대 20턴.
// 티어 설정(MOAI_MODEL_HIGH) 시 세션 동안 High 모델을 사용(플랜 품질). 다시 입력하면 토글 오프.
internal sealed class BrainstormCommand : ISlashCommand
{
    internal const int MaxTurns = 20;

    public string Name => "brainstorming";
    public string Description => L10n.Get("slash.brainstorm.description");

    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        var st = ctx.State;

        // 이미 활성이면 토글 오프(모델 복원).
        if (st.Brainstorming)
        {
            End(ctx);
            return Task.FromResult(new SlashResult(L10n.Get("slash.brainstorm.ended")));
        }

        st.Brainstorming = true;
        st.BrainstormTurnsLeft = MaxTurns;
        st.BrainstormBasePlan = ctx.PlanTree?.Invoke() ?? string.Empty;

        // 인터뷰어 역할 리마인더 주입(세션 동안 유지).
        ctx.Engine.AddSystemReminder(MoaiCode.Core.Agent.Prompts.Reminders.Brainstorm);

        var topic = string.Join(' ', args).Trim();
        var banner = L10n.Get("slash.brainstorm.started", MaxTurns);
        return Task.FromResult(topic.Length > 0
            ? new SlashResult(banner, SubmitPrompt: topic)
            : new SlashResult(banner + "\n" + L10n.Get("slash.brainstorm.askTopic")));
    }

    // 브레인스토밍 종료: 상태 해제. ReplApp(플랜 감지/한도 도달)과 공용.
    internal static void End(SlashContext ctx)
    {
        var st = ctx.State;
        st.Brainstorming = false;
        st.BrainstormTurnsLeft = 0;
        st.BrainstormBasePlan = null;
    }
}

internal sealed class CostCommand : ISlashCommand
{
    public string Name => "cost";
    public string Description => L10n.Get("slash.cost.description");
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
    public string Description => L10n.Get("slash.permissions.description");

    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        var rules = ctx.Rules;
        if (rules is null)
        {
            return Task.FromResult(new SlashResult(L10n.Get("slash.permissions.noStore")));
        }

        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        var rest = string.Join(' ', args.Skip(1)).Trim();

        switch (sub)
        {
            case "allow" when rest.Length > 0:
                rules.AddAllow(rest);
                return Task.FromResult(new SlashResult(L10n.Get("slash.permissions.allowAdded", rest)));
            case "deny" when rest.Length > 0:
                rules.AddDeny(rest);
                return Task.FromResult(new SlashResult(L10n.Get("slash.permissions.denyAdded", rest)));
            case "remove" when rest.Length > 0:
                var ok = rules.Remove(rest);
                return Task.FromResult(new SlashResult(ok
                    ? L10n.Get("slash.permissions.removed", rest)
                    : L10n.Get("slash.permissions.notFound", rest)));
            case "allow":
            case "deny":
            case "remove":
                return Task.FromResult(new SlashResult(L10n.Get("slash.permissions.usage")));
            default:
                Render(rules);
                return Task.FromResult(new SlashResult(string.Empty));
        }
    }

    private static void Render(IPermissionRuleStore rules)
    {
        Spectre.Console.AnsiConsole.WriteLine();
        Spectre.Console.AnsiConsole.MarkupLine($"[aqua]{Spectre.Console.Markup.Escape(L10n.Get("slash.permissions.title"))}[/] [grey70]· {Spectre.Console.Markup.Escape(L10n.Get("slash.permissions.savedIn"))}[/]");
        Spectre.Console.AnsiConsole.MarkupLine("[green]allow[/] " + (rules.Allow.Count == 0 ? $"[grey58]{Spectre.Console.Markup.Escape(L10n.Get("common.none"))}[/]" : ""));
        foreach (var r in rules.Allow)
        {
            Spectre.Console.AnsiConsole.MarkupLine($"  [grey85]{Spectre.Console.Markup.Escape(r)}[/]");
        }

        Spectre.Console.AnsiConsole.MarkupLine("[red]deny[/] " + (rules.Deny.Count == 0 ? $"[grey58]{Spectre.Console.Markup.Escape(L10n.Get("common.none"))}[/]" : ""));
        foreach (var r in rules.Deny)
        {
            Spectre.Console.AnsiConsole.MarkupLine($"  [grey85]{Spectre.Console.Markup.Escape(r)}[/]");
        }

        Spectre.Console.AnsiConsole.MarkupLine(
            $"[grey58]{Spectre.Console.Markup.Escape(L10n.Get("slash.permissions.hint"))}[/]");
    }
}

// /usage: 모델별 로컬 토큰 사용량만 표시. Spectre 로 직접 렌더(색/정렬)하고 빈 결과 반환.
internal sealed class UsageCommand : ISlashCommand
{
    public string Name => "usage";
    public string Description => L10n.Get("slash.usage.description");

    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        var usage = ctx.Usage;
        var rows = usage?.All() ?? System.Array.Empty<ModelUsage>();
        var since = usage is not null ? L10n.Get("slash.usage.since", usage.Since.ToString("yyyy-MM-dd")) : "";

        Spectre.Console.AnsiConsole.WriteLine();
        Spectre.Console.AnsiConsole.MarkupLine($"[grey70]{Spectre.Console.Markup.Escape(L10n.Get("slash.usage.title", since))}[/]");

        if (rows.Count == 0)
        {
            Spectre.Console.AnsiConsole.MarkupLine($"[grey70]{Spectre.Console.Markup.Escape(L10n.Get("slash.usage.empty"))}[/]");
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
            $"[grey70]  {Spectre.Console.Markup.Escape(L10n.Get("slash.usage.total").PadRight(nameW))}[/] " +
            $"[grey70]in[/] [white]{ti,11:N0}[/]  " +
            $"[grey70]out[/] [white]{to,10:N0}[/]  " +
            $"[grey70]turns {tt}[/]");

        return Task.FromResult(new SlashResult(""));
    }
}

internal sealed class PlanModeCommand : ISlashCommand
{
    public string Name => "plan";
    public string Description => L10n.Get("slash.plan.description");
    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        ctx.State.Mode = AgentMode.Plan;
        ctx.Engine.AddSystemReminder(MoaiCode.Core.Agent.Prompts.Reminders.PlanMode);
        return Task.FromResult(new SlashResult(L10n.Get("slash.plan.switched")));
    }
}

internal sealed class ActModeCommand : ISlashCommand
{
    public string Name => "act";
    public string Description => L10n.Get("slash.act.description");
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
    public string Description => L10n.Get("slash.checkpoint.description");
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
    public string Description => L10n.Get("slash.checkpoints.description");
    public async Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        var list = await ctx.Checkpoints.ListAsync(ct).ConfigureAwait(false);
        if (list.Count == 0)
        {
            return new SlashResult(L10n.Get("slash.checkpoints.none"));
        }

        var lines = list.Select(c => $"{c.Id}\t{c.CreatedAt:yyyy-MM-dd HH:mm:ss}\t{c.Subject}");
        return new SlashResult(string.Join("\n", lines));
    }
}

internal sealed class CheckpointDiffCommand : ISlashCommand
{
    public string Name => "checkpoint-diff";
    public string Description => L10n.Get("slash.checkpointDiff.description");
    public async Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        var diff = await ctx.Checkpoints.DiffAsync(args.FirstOrDefault(), ct).ConfigureAwait(false);
        return new SlashResult(diff);
    }
}

internal sealed class RestoreCommand : ISlashCommand
{
    public string Name => "restore";
    public string Description => L10n.Get("slash.restore.description");
    public async Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            return new SlashResult(L10n.Get("slash.restore.usage"));
        }

        await ctx.Checkpoints.RestoreAsync(args[0], ct).ConfigureAwait(false);
        return new SlashResult($"restored checkpoint: {args[0]}");
    }
}

internal sealed class SessionsCommand : ISlashCommand
{
    public string Name => "sessions";
    public string Description => L10n.Get("slash.sessions.description");
    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
        => SessionPicker.RunAsync(ctx, ct);
}

internal sealed class SaveCommand : ISlashCommand
{
    public string Name => "save";
    public string Description => L10n.Get("slash.save.description");
    public async Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            return new SlashResult(L10n.Get("slash.save.usage"));
        }

        await ctx.Sessions.SaveAsync(args[0], ctx.Engine.Messages, ct).ConfigureAwait(false);
        return new SlashResult(L10n.Get("slash.save.done", args[0], ctx.Engine.Messages.Count));
    }
}

internal sealed class ResumeCommand : ISlashCommand
{
    public string Name => "resume";
    public string Description => L10n.Get("slash.resume.description");
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
                return new SlashResult(L10n.Get("slash.resume.badNumber", n, infos.Count));
            }

            id = infos[n - 1].Id;
        }

        return await SessionPicker.ResumeAsync(ctx, id, ct).ConfigureAwait(false);
    }
}

internal sealed class HistoryCommand : ISlashCommand
{
    public string Name => "history";
    public string Description => L10n.Get("slash.history.description");
    public async Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        var recent = await ctx.History.RecentAsync(20, ct).ConfigureAwait(false);
        return new SlashResult(
            recent.Count == 0 ? L10n.Get("slash.history.none") : string.Join("\n", recent));
    }
}

internal sealed class InitCommand : ISlashCommand
{
    public string Name => "init";
    public string Description => L10n.Get("slash.init.description");
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
    public string Description => L10n.Get("slash.review.description");
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
    public string Description => L10n.Get("slash.securityReview.description");
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
    public string Description => L10n.Get("slash.bughunter.description");
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
    public string Description => L10n.Get("slash.simplify.description");
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
    public string Description => L10n.Get("slash.help.description");
    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        var lines = _all.Select(c => $"/{c.Name} — {c.Description}");
        return Task.FromResult(new SlashResult(string.Join("\n", lines)));
    }
}
