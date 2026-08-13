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

    // 라이브 세션의 모델을 화살표 선택으로 교체한다. 대화형이면 먼저 "모델별 업무 분할
    // (난이도별 다른 모델)" 여부를 묻고, 예 → 하급·중급·고급 순 선택, 아니오 → 단일 모델 선택.
    public async Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        var mc = ctx.Models;
        if (mc is null)
        {
            return new SlashResult(L10n.Get("slash.model.unsupported", ctx.ProviderDesc));
        }

        // /model (대화형): 먼저 "모델별 업무 분할 여부"를 묻는다.
        // 예 → 하급·중급·고급 순으로 각 티어 모델 선택(난이도 라우팅). 아니오 → 단일 모델 선택.
        if (!Console.IsInputRedirected)
        {
            var split = SelectList.Prompt(
                L10n.Get("slash.model.splitPrompt"),
                new[] { L10n.Get("slash.model.splitYes"), L10n.Get("slash.model.splitNo") },
                0);
            if (split < 0)
            {
                return new SlashResult(L10n.Get("common.unchanged"));
            }

            if (split == 0)
            {
                return await ConfigureTiersAsync(mc, ctx, ct).ConfigureAwait(false);
            }
            // split == 1 → 아래 단일 모델 선택으로 진행.
        }

        Spectre.Console.AnsiConsole.MarkupLine($"[grey70]{Spectre.Console.Markup.Escape(L10n.Get("slash.model.fetching"))}[/]");
        var models = await mc.ListModelsAsync(ct).ConfigureAwait(false);
        if (models.Count == 0)
        {
            return new SlashResult(L10n.Get("slash.model.fetchFailed"));
        }

        // 현재 모델을 기본 선택으로.
        var list = models.ToList();
        var defIdx = list.FindIndex(m => string.Equals(m, mc.CurrentModel, StringComparison.OrdinalIgnoreCase));
        if (defIdx < 0)
        {
            defIdx = 0;
        }

        var pick = SelectList.Prompt(L10n.Get("slash.model.pickTitle"), models, defIdx);
        if (pick < 0)
        {
            return new SlashResult(L10n.Get("common.unchanged"));
        }

        var chosen = models[pick];
        mc.CurrentModel = chosen;          // 라이브 반영 (QueryEngine/서브에이전트/컴팩션 공유 인스턴스)
        ctx.PersistModel?.Invoke(chosen);  // settings.json + env 저장 (다음 실행에도 유지)
        return new SlashResult(L10n.Get("slash.model.changed", chosen));
    }

    // 난이도별(하급/중급/고급) 모델을 순차로 선택·저장한다. 각 티어에서 취소(Esc)하면 그 티어는 변경하지 않는다.
    private static async Task<SlashResult> ConfigureTiersAsync(
        MoaiCode.Core.Agent.IModelControl mc, SlashContext ctx, CancellationToken ct)
    {
        Spectre.Console.AnsiConsole.MarkupLine($"[grey70]{Spectre.Console.Markup.Escape(L10n.Get("slash.model.fetching"))}[/]");
        var avail = await mc.ListModelsAsync(ct).ConfigureAwait(false);
        if (avail.Count == 0)
        {
            return new SlashResult(L10n.Get("slash.model.fetchFailed"));
        }

        var summary = new System.Text.StringBuilder(L10n.Get("slash.model.tierSummary") + "\n");
        foreach (var tier in new[] { "low", "mid", "high" })
        {
            var label = L10n.Get(tier switch { "low" => "slash.model.tierLow", "high" => "slash.model.tierHigh", _ => "slash.model.tierMid" });
            var envKey = tier switch { "low" => "MOAI_MODEL_LOW", "high" => "MOAI_MODEL_HIGH", _ => "MOAI_MODEL_MID" };
            var opts = new List<string> { L10n.Get("slash.model.tierClearOption") };
            opts.AddRange(avail);
            var cur = Environment.GetEnvironmentVariable(envKey);
            var def = cur is null ? 0 : Math.Max(0, opts.FindIndex(o => string.Equals(o, cur, StringComparison.OrdinalIgnoreCase)));
            var pick = SelectList.Prompt(L10n.Get("slash.model.tierPickTitle", label, tier), opts, def < 0 ? 0 : def);
            if (pick < 0)
            {
                summary.AppendLine($"  {label}: {L10n.Get("slash.model.tierUnchanged")}");
                continue;
            }

            var tierModel = pick == 0 ? null : opts[pick];
            ctx.PersistTierModel?.Invoke(tier, tierModel);
            summary.AppendLine($"  {label}: {tierModel ?? L10n.Get("slash.model.tierDefault")}");
        }

        return new SlashResult(summary.ToString().TrimEnd());
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
        // /skills sync — 팀 공유 스킬을 서버에서 다시 받아 라이브로 갱신.
        if (args.Length > 0 && string.Equals(args[0], "sync", StringComparison.OrdinalIgnoreCase))
        {
            if (ctx.SyncTeamSkills is null)
            {
                return new SlashResult(L10n.Get("slash.skills.syncUnavailable"));
            }

            return new SlashResult(await ctx.SyncTeamSkills(ct).ConfigureAwait(false));
        }

        // /skills — 대화형이면 체크박스 피커로 활성/비활성 토글, 아니면 목록만 출력.
        if (ctx.GetSkillChoices is not null && ctx.SetDisabledSkills is not null && !Console.IsInputRedirected)
        {
            var choices = ctx.GetSkillChoices();
            if (choices.Count == 0)
            {
                return new SlashResult(L10n.Get("slash.skills.none"));
            }

            var labels = choices.Select(c => $"{c.Name}  ({c.Source})").ToList();
            var initial = choices.Select(c => c.Enabled).ToList();
            var picked = MultiSelectList.Prompt(L10n.Get("slash.skills.pickerTitle"), labels, initial);
            if (picked is null)
            {
                return new SlashResult(L10n.Get("slash.skills.cancelled"));
            }

            var on = picked.ToHashSet();
            var disabled = choices.Where((c, i) => !on.Contains(i)).Select(c => c.Name).ToList();
            return new SlashResult(ctx.SetDisabledSkills(disabled));
        }

        return new SlashResult(ctx.SkillNames.Count == 0
            ? L10n.Get("slash.skills.none")
            : L10n.Get("slash.skills.list", string.Join(", ", ctx.SkillNames)));
    }
}

// /login: 세션 도중 재로그인. 자격증명이 만료·손상되면 REPL 을 나가지 않고 여기서 복구한다.
internal sealed class LoginCommand : ISlashCommand
{
    public string Name => "login";
    public string Description => L10n.Get("slash.login.description");

    public async Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        if (ctx.Login is null)
        {
            return new SlashResult(L10n.Get("slash.login.unavailable"));
        }

        return new SlashResult(await ctx.Login(ct).ConfigureAwait(false));
    }
}

internal sealed class LogoutCommand : ISlashCommand
{
    public string Name => "logout";
    public string Description => L10n.Get("slash.logout.description");

    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
        => Task.FromResult(new SlashResult(
            ctx.Logout is null ? L10n.Get("slash.login.unavailable") : ctx.Logout()));
}

// /install·/uninstall: Windows 셸 통합(PATH + 탐색기 우클릭 "MoAI Code로 열기").
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
    public string Description => "현재 실행 계획(Phase 진행 트리) 표시";
    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        var tree = ctx.PlanTree?.Invoke();
        return Task.FromResult(new SlashResult(string.IsNullOrWhiteSpace(tree) ? "(활성 플랜 없음)" : tree));
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

// /usage: 로그인 계정·시간 + 모델별 로컬 토큰 사용량. Spectre 로 직접 렌더(색/정렬)하고 빈 결과 반환.
internal sealed class UsageCommand : ISlashCommand
{
    public string Name => "usage";
    public string Description => L10n.Get("slash.usage.description");

    public Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        var ac = ctx.Account;
        var email = string.IsNullOrWhiteSpace(ac?.Email) ? L10n.Get("common.unknown") : ac!.Email!;
        var host = string.IsNullOrWhiteSpace(ac?.Host) ? L10n.Get("common.notSet") : ac!.Host!;
        var org = string.IsNullOrWhiteSpace(ac?.OrgName) ? null : ac!.OrgName!;

        Spectre.Console.AnsiConsole.WriteLine();
        Spectre.Console.AnsiConsole.MarkupLine(
            $"[aqua]{Spectre.Console.Markup.Escape(L10n.Get("slash.usage.account"))}[/] [grey85]{Spectre.Console.Markup.Escape(email)}[/] [grey70]· {Spectre.Console.Markup.Escape(host)}[/]");
        if (org is not null)
        {
            Spectre.Console.AnsiConsole.MarkupLine(
                $"[grey70]{Spectre.Console.Markup.Escape(L10n.Get("slash.usage.org"))}[/][grey85]{Spectre.Console.Markup.Escape(org)}[/]");
        }
        Spectre.Console.AnsiConsole.MarkupLine(
            $"[grey70]{Spectre.Console.Markup.Escape(L10n.Get("slash.usage.loginNow", FormatTime(ac?.LoginAt), DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm")))}[/]");

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

    private static string FormatTime(string? iso)
        => !string.IsNullOrWhiteSpace(iso) && DateTimeOffset.TryParse(iso, out var t)
            ? t.ToString("yyyy-MM-dd HH:mm")
            : L10n.Get("common.unknown");
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
