using System.Runtime.InteropServices;
using MoaiCode.Config;
using MoaiCode.Core.Agent;
using MoaiCode.Core.Agent.Prompts;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;
using MoaiCode.Mcp;
using MoaiCode.Mcp.Skills;
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
        ApplySettingsToEnv(settings);

        // 2) 자격증명: env에 없으면 저장소에서 주입
        ResolveCredentials();

        var model = ProviderFactory.CreateDefault(out var providerDesc);
        if (verbose)
        {
            Console.WriteLine($"provider: {providerDesc}");
        }

        var toolList = new List<ITool>(ToolRegistry.BuiltIn) { new BashTool() };

        // 3) MCP 서버
        var mcpConfigs = McpConfigLoader.Discover(cwd);
        var mcp = new McpManager();
        if (mcpConfigs.Count > 0)
        {
            if (verbose)
            {
                Console.WriteLine($"connecting {mcpConfigs.Count} MCP server(s)…");
            }

            await mcp.ConnectAllAsync(mcpConfigs, ct).ConfigureAwait(false);
            toolList.AddRange(mcp.Tools);

            if (verbose)
            {
                foreach (var err in mcp.Errors)
                {
                    Console.WriteLine($"  mcp warn: {err}");
                }
            }
        }

        // 4) 스킬 (+ 플러그인 스킬 + 기본 번들 스킬)
        var skills = new List<Skill>(SkillLoader.Discover(cwd));
        skills.AddRange(PluginLoader.Discover(cwd));

        // 기본 번들 스킬은 최저 우선순위 — 같은 이름의 사용자 스킬이 있으면 그쪽을 우선.
        BundledSkills.EnsureExtracted();
        var haveSkills = new HashSet<string>(skills.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var s in SkillLoader.LoadFromDir(BundledSkills.Dir))
        {
            if (haveSkills.Add(s.Name))
            {
                skills.Add(s);
            }
        }
        if (skills.Count > 0)
        {
            toolList.Add(new SkillTool(skills));
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
        IPermissionGate baseGate = settings.Permission switch
        {
            PermissionMode.Auto => new AutoApproveGate(),
            PermissionMode.Deny => new DenyAllGate(),
            _ => interactive ? new SpectrePermissionGate() : new AutoApproveGate(),
        };
        // 워크스페이스 밖 절대경로 쓰기 확인용 프롬프트 (대화형에서만; 비대화형이면 confine 시 거부).
        var confirmer = interactive ? new SpectrePermissionGate() : (IPermissionGate?)null;
        IPermissionGate gate = new ModeAwarePermissionGate(
            state, baseGate, cwd, settings.ConfineToWorkspace, confirmer);

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
            providerDesc);

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
            catch (IOException)
            {
                // skip unreadable
            }
        }

        return parts.Count == 0 ? null : string.Join("\n\n", parts);
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
