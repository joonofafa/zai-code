using System.Runtime.InteropServices;
using System.Security;
using MoaiCode.Config;
using MoaiCode.Core.Agent;
using MoaiCode.Core.Agent.Prompts;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Security;
using MoaiCode.Core.Tools;
using MoaiCode.Mcp;
using MoaiCode.Mcp.Skills;
using MoaiCode.Localization;
using MoaiCode.Persistence;
using MoaiCode.Providers;
using MoaiCode.Tools;
using MoaiCode.Tools.Agent;
using MoaiCode.Tools.Bash;
using MoaiCode.Tools.Tasks;
using MoaiCode.Tui;
using MoaiCode.Tui.Commands;

namespace MoaiCode.Cli;

/// <summary>REPL/headless 양쪽이 공유하는 런타임 구성 결과. Mcp는 사용 후 dispose 필요.</summary>
public sealed record AppRuntime(
    McpManager Mcp,
    SlashContext Ctx,
    IReadOnlyList<ITool> Tools,
    IReadOnlyList<string> SkillNames,
    IReadOnlyList<McpServerConfig> McpConfigs,
    string ProviderDesc,
    Settings Settings);

/// <summary>프로바이더/툴/MCP/스킬/엔진을 조립 (TS의 cli 부트스트랩 대응).</summary>
public static class AppBootstrap
{


    public static async Task<AppRuntime> BuildAsync(bool interactive, bool verbose, CancellationToken ct)
    {
        var cwd = Directory.GetCurrentDirectory();

        // 1) 설정 머지 (user → project → env)
        var settings = SettingsLoader.Load(cwd);
        L10n.SetLanguage(settings.Language);
        ApplySettingsToEnv(settings);

        // 2) 자격증명: env에 없으면 저장소에서 주입
        ResolveCredentials();

        var model = ProviderFactory.CreateDefault(out var providerDesc);
        if (verbose)
        {
            Console.WriteLine("Provider: BCCard AI Department");
            Console.WriteLine($"Version: {Banner.VersionString()}");
        }

        var toolList = new List<ITool>(ToolRegistry.BuiltIn) { new BashTool() };

        // 닫힌 Office 문서 생성/검증(Open XML) — 전 플랫폼.
        toolList.Add(new MoaiCode.Tools.OpenXml.DocxCreateTool());
        toolList.Add(new MoaiCode.Tools.OpenXml.XlsxCreateTool());
        toolList.Add(new MoaiCode.Tools.OpenXml.PptxCreateTool());
        toolList.Add(new MoaiCode.Tools.OpenXml.OfficeDocInspectTool());

        // 로컬 문서 청킹(오프라인, 서버무관) — 청크 생성/가져오기/벡터 검색.
        toolList.Add(new MoaiCode.Tools.OpenXml.ChunkBuildTool());
        toolList.Add(new MoaiCode.Tools.OpenXml.ChunkFetchTool());
        toolList.Add(new MoaiCode.Tools.OpenXml.ChunkSearchTool());

        // COM Office 편집 툴('열려있는 문서 편집')은 CLI 에서 제외 — MoAI Desktop(GUI) 전용.
        // (닫힌 문서 생성/검증 Open XML 툴은 위에서 이미 등록되어 CLI 에도 유지.)

        // 3) MCP 서버
        var mcpConfigs = McpConfigLoader.Discover(cwd);
        var mcp = new McpManager();
        if (mcpConfigs.Count > 0)
        {
            if (verbose)
            {
                Console.WriteLine($"connecting {mcpConfigs.Count} MCP server(s)…");
            }

            try
            {
                await mcp.ConnectAllAsync(mcpConfigs, ct).ConfigureAwait(false);
                toolList.AddRange(mcp.Tools);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"mcp error: failed to connect mcp servers: {ex.Message}");
            }

            if (verbose)
            {
                foreach (var err in mcp.Errors)
                {
                    Console.WriteLine($"  mcp warn: {err}");
                }
            }
        }

        // 4) 스킬 — 우선순위 user > plugin > team > bundled. 로컬 비활성(/skills)로 끈 스킬은 제외.
        // 팀 공유 스킬은 로그인 상태에서만 sync. 네트워크/인증 실패는 non-fatal(로컬/번들 스킬은 계속 동작).
        var teamApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var teamBaseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
        if (!string.IsNullOrWhiteSpace(teamApiKey) && !string.IsNullOrWhiteSpace(teamBaseUrl))
        {
            var sync = await TeamSkills.SyncAsync(teamBaseUrl!, teamApiKey!, ct).ConfigureAwait(false);
            if (sync.Error is not null)
            {
                // 사용자에겐 친화 메시지(빨강)만. 원문 오류는 콘솔에 찍지 않고 로그 파일로만 남긴다.
                Console.WriteLine($"\x1b[31m{L10n.Get("cli.skills.teamLoadFailed")}\x1b[0m");
                MoaiLog.Warn($"team skills sync failed: {sync.Error}");
            }
        }
        BundledSkills.EnsureExtracted();

        var disabledSkills = SkillState.LoadDisabled();
        var skills = SkillCatalog.DiscoverAll(cwd)
            .Where(x => !disabledSkills.Contains(x.Skill.Name))
            .Select(x => x.Skill)
            .ToList();
        var skillTool = new SkillTool(skills);
        if (skills.Count > 0)
        {
            toolList.Add(skillTool);
        }

        // 4b) Agent/Task 툴 (서브에이전트는 현재 툴 스냅샷을 사용 — 재귀 방지)
        var subTools = new List<ITool>(toolList);
        toolList.Add(new AgentTool(model, subTools));
        var taskStore = new TaskStore();
        toolList.Add(new TaskCreateTool(taskStore));
        toolList.Add(new TaskListTool(taskStore));
        toolList.Add(new TaskUpdateTool(taskStore));

        // 대화형에서만 사용자 선택 툴 제공 (헤드리스/파이프에선 물어볼 수 없음).
        if (interactive)
        {
            toolList.Add(new AskUserQuestionTool());
        }

        var state = new AgentRuntimeState();
        var checkpoints = new CheckpointStore(cwd);

        // 5) 권한 게이트 (설정 우선, ask는 대화형에서만 프롬프트)
        // 보안: 비대화형(헤드리스/파이프)에서 ask 는 물어볼 수 없다. 예전엔 조용히 전면 자동승인이라
        // 위험했다 → 이제 기본은 '거부'하고, 자동승인은 명시적 opt-in(MOAI_YES=1 등 / permission=auto)만.
        // 영속 권한 규칙(Claude Code permissions.allow/deny). "항상 허용" 선택 시 여기에 저장된다.
        var rules = PermissionRules.LoadDefault(settings);
        void PersistAllow(string pattern) => rules.AddAllow(pattern);

        var headlessApprove = HeadlessAutoApprove();
        IPermissionGate baseGate = settings.Permission switch
        {
            PermissionMode.Auto => new AutoApproveGate(),
            PermissionMode.Deny => new DenyAllGate(),
            _ => interactive
                ? new SpectrePermissionGate(persistAllow: PersistAllow)
                : (headlessApprove ? new AutoApproveGate() : new DenyAllGate()),
        };
        if (!interactive && settings.Permission == PermissionMode.Ask && !headlessApprove)
        {
            Console.Error.WriteLine(L10n.Get("permission.headlessDenied"));
        }
        // 확인 프롬프트(원격 실행/파괴적 명령/워크스페이스 밖 쓰기). 대화형에서만; 비대화형이면 거부.
        // "항상 허용"은 명령 prefix 스코프(Bash(ssh moai-ec2))로만 저장되므로 무차별 통과가 되지 않는다.
        var confirmer = interactive
            ? new SpectrePermissionGate(persistAllow: PersistAllow)
            : (IPermissionGate?)null;

        // 규칙이 모르는 위험을 맥락으로 판정. 자동 승인이 일어나는 경로에서만 호출된다.
        // MOAI_RISK_CLASSIFIER=0 으로 끌 수 있다(오프라인/지연 민감 환경).
        var classifier = RiskClassifierEnabled() ? new LlmRiskClassifier(model) : null;

        IPermissionGate gate = new ModeAwarePermissionGate(
            state, baseGate, cwd, settings.ConfineToWorkspace, confirmer, classifier, rules);

        var observer = new HarnessToolObserver(settings, checkpoints);
        var engine = new QueryEngine(
            model, toolList, gate, observer, settings.MaxTurns,
            contextWindowTokens: settings.ContextWindowTokens,
            pendingTasks: () => taskStore.All().Any(t => t.Status != MoaiCode.Tools.Tasks.TaskStatus.Completed));
        var promptCtx = BuildPromptContext(cwd, settings, toolList);
        engine.Seed(new[] { new SystemMessage(SystemPromptBuilder.Build(promptCtx)) });

        var ctx = new SlashContext(
            engine,
            new SessionStore(),
            new HistoryStore(),
            checkpoints,
            state,
            toolList.Select(t => t.Name).ToList(),
            skills.Select(s => s.Name).ToList(),
            mcpConfigs.Select(c => c.Name).ToList(),
            providerDesc,
            // /model 로 라이브 전환 (지원 모델이면 non-null) + 선택 시 settings/env 영속화.
            model as IModelControl,
            chosen =>
            {
                SettingsWriter.Set(new Dictionary<string, string?> { ["model"] = chosen });
                Environment.SetEnvironmentVariable("MOAI_MODEL", chosen);
            },
            // /usage: 모델별 로컬 토큰 누적 + 로그인 계정/시각.
            new UsageStore(),
            new AccountInfo(settings.Account, settings.Host ?? settings.BaseUrl, settings.LoginAt, settings.OrgName),
            rules,
            settings.ReasoningEffort,
            effort =>
            {
                SettingsWriter.Set(new Dictionary<string, string?> { ["reasoningEffort"] = effort });
                Environment.SetEnvironmentVariable("MOAI_REASONING_EFFORT", effort);
            },
            settings.Language,
            language =>
            {
                L10n.SetLanguage(language);
                SettingsWriter.Set(new Dictionary<string, string?> { ["language"] = language });
                Environment.SetEnvironmentVariable("MOAI_LANGUAGE", language);
            },
            // /skills sync: 팀 공유 스킬을 다시 받아 디스크에 기록하고 라이브 SkillTool 을 재적재.
            SyncTeamSkills: async token =>
            {
                var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                var burl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(burl))
                {
                    return L10n.Get("slash.skills.loginRequired");
                }

                var res = await TeamSkills.SyncAsync(burl!, key!, token).ConfigureAwait(false);
                if (res.Error is not null)
                {
                    return L10n.Get("slash.skills.syncFailed", res.Error);
                }

                // 로컬 비활성 필터를 유지한 채 라이브 SkillTool 재적재.
                var dis = SkillState.LoadDisabled();
                var reloaded = SkillCatalog.DiscoverAll(cwd)
                    .Where(x => !dis.Contains(x.Skill.Name)).Select(x => x.Skill).ToList();
                skillTool.Reload(reloaded);
                return L10n.Get("slash.skills.synced", res.Written, reloaded.Count);
            },
            // /skills: 전체 스킬(이름·출처·현재 활성) 조회.
            GetSkillChoices: () =>
            {
                var dis = SkillState.LoadDisabled();
                return SkillCatalog.DiscoverAll(cwd)
                    .Select(x => (x.Skill.Name, x.Source, Enabled: !dis.Contains(x.Skill.Name)))
                    .ToList();
            },
            // /skills: 비활성 목록 저장 + 라이브 SkillTool 재적재.
            SetDisabledSkills: disabledNames =>
            {
                SkillState.SaveDisabled(disabledNames);
                var dis = new HashSet<string>(disabledNames, StringComparer.OrdinalIgnoreCase);
                var active = SkillCatalog.DiscoverAll(cwd)
                    .Where(x => !dis.Contains(x.Skill.Name)).Select(x => x.Skill).ToList();
                skillTool.Reload(active);
                return L10n.Get("slash.skills.toggleSaved", active.Count, dis.Count);
            });

        return new AppRuntime(
            mcp, ctx, toolList, skills.Select(s => s.Name).ToList(), mcpConfigs, providerDesc, settings);
    }

    private static PromptContext BuildPromptContext(string cwd, Settings settings, IReadOnlyList<ITool> tools)
    {
        var platform = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsMacOS() ? "macos"
            : OperatingSystem.IsLinux() ? "linux"
            : "unknown";

        var model = settings.Model
            ?? Environment.GetEnvironmentVariable("MOAI_MODEL")
            ?? Environment.GetEnvironmentVariable("OPENAI_MODEL");
        var modelDescription = string.IsNullOrEmpty(model)
            ? null
            : $"You are powered by the model {model}.";

        return new PromptContext
        {
            WorkingDirectory = cwd,
            IsGitRepo = Directory.Exists(Path.Combine(cwd, ".git")),
            Platform = platform,
            OsVersion = RuntimeInformation.OSDescription,
            CurrentDate = DateTimeOffset.Now.ToString("yyyy-MM-dd"),
            ModelDescription = modelDescription,
            ClaudeMd = LoadProjectInstructions(cwd),
            RepoMap = RepoMapBuilder.Build(cwd, settings.RepoMapTokens),
            OutputStyle = settings.OutputStyle,
            ToolNames = tools.Select(t => t.Name).ToList(),
        };
    }

    // CLAUDE.md / AGENTS.md 수집 (cwd 우선, 그다음 사용자 홈). 총 길이 제한.
    private static string? LoadProjectInstructions(string cwd)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(cwd, "CLAUDE.md"),
            Path.Combine(cwd, "AGENTS.md"),
            Path.Combine(cwd, ".claude", "CLAUDE.md"),
            Path.Combine(home, ".moai", "CLAUDE.md"),
            Path.Combine(home, ".claude", "CLAUDE.md"),
        };

        var parts = new List<string>();
        var total = 0;
        const int cap = 12000;
        foreach (var path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var text = File.ReadAllText(path).Trim();
                if (text.Length == 0 || total + text.Length > cap)
                {
                    continue;
                }

                parts.Add($"## {path}\n{text}");
                total += text.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or PathTooLongException)
            {
                // skip unreadable or restricted files safely
            }
        }

        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    // 비대화형에서 위험 툴 자동 승인 opt-in. MOAI_YES / MOAI_APPROVE / MOAI_AUTO_APPROVE = 1/true/yes.
    // 위험 판정 분류기 on/off. 기본 켜짐 — MOAI_RISK_CLASSIFIER=0/false/off 로 끈다.
    private static bool RiskClassifierEnabled()
    {
        var v = Environment.GetEnvironmentVariable("MOAI_RISK_CLASSIFIER");
        if (string.IsNullOrEmpty(v))
        {
            return true;
        }

        return !(v.Equals("0", StringComparison.Ordinal)
                 || v.Equals("false", StringComparison.OrdinalIgnoreCase)
                 || v.Equals("off", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HeadlessAutoApprove()
    {
        foreach (var name in new[] { "MOAI_YES", "MOAI_APPROVE", "MOAI_AUTO_APPROVE" })
        {
            var v = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(v)
                && (v.Equals("1", StringComparison.Ordinal)
                    || v.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || v.Equals("yes", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static void ApplySettingsToEnv(Settings s)
    {
        if (!string.IsNullOrEmpty(s.Model)
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MOAI_MODEL"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENAI_MODEL")))
        {
            Environment.SetEnvironmentVariable("MOAI_MODEL", s.Model);
        }

        if (!string.IsNullOrEmpty(s.BaseUrl)
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENAI_BASE_URL")))
        {
            Environment.SetEnvironmentVariable("OPENAI_BASE_URL", s.BaseUrl);
        }

        if (!string.IsNullOrEmpty(s.ReasoningEffort)
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MOAI_REASONING_EFFORT"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENAI_REASONING_EFFORT"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MOAI_EFFORT")))
        {
            Environment.SetEnvironmentVariable("MOAI_REASONING_EFFORT", s.ReasoningEffort);
        }
    }

    private static void ResolveCredentials()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
        {
            return;
        }

        var stored = new FileCredentialStore().Get("OPENAI_API_KEY");
        if (!string.IsNullOrEmpty(stored))
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", stored);
        }
    }
}
