using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using MoaiCode.Config;
using MoaiCode.Core.Agent;
using MoaiCode.Core.Agent.Prompts;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;
using MoaiCode.Mcp.Skills;
using MoaiCode.Providers;
using MoaiCode.Tools;
using MoaiCode.Tools.OpenXml;

namespace MoaiCode.Gui.Agent;

/// <summary>
/// GUI용 코어 조립. QueryEngine 을 in-process 로 만든다(데몬·IPC 없음). 일반 사용자 안전을 위해
/// Bash·SQL 툴을 제외한 큐레이션 셋만 등록하고, 파일 쓰기는 워크스페이스(내 문서\MoAI)로 confine.
/// </summary>
public static class GuiBootstrap
{
    public static string Workspace
    {
        get
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(docs))
            {
                docs = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            var ws = Path.Combine(docs, "MoAI");
            Directory.CreateDirectory(ws);
            return ws;
        }
    }

    /// <summary>엔진을 만든다. 자격증명이 없거나 실패하면 null + error 사유.</summary>
    public static QueryEngine? TryBuild(IPermissionGate gate, out string workspace, out string? error)
    {
        workspace = Workspace;
        error = null;
        try
        {
            var settings = SettingsLoader.Load(workspace);
            ApplySettingsToEnv(settings);
            ResolveCredentials();

            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
            {
                error = "로그인이 필요합니다. `moai login` 으로 API 키를 저장한 뒤 다시 실행하세요.";
                return null;
            }

            var model = ProviderFactory.CreateDefault(out _);
            var tools = CuratedTools();
            var engine = new QueryEngine(
                model, tools, gate, observer: null,
                maxTurns: settings.MaxTurns,
                workingDirectory: workspace,
                contextWindowTokens: settings.ContextWindowTokens);
            engine.Seed(new Message[]
            {
                new SystemMessage(SystemPromptBuilder.Build(BuildPromptContext(workspace, settings, tools))),
            });
            return engine;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    // 일반 사용자 안전 서브셋 — Bash(미참조)·OrgDatas(SQL) 제외, 문서/지식/청킹 포함.
    private static IReadOnlyList<ITool> CuratedTools()
    {
        var list = ToolRegistry.BuiltIn
            .Where(t => t.Name is not "OrgDatas" and not "OrgDatasList" and not "OrgDocsDelete")
            .ToList();

        // 템플릿(디자인/양식) 문서는 OpenXML 로 '생성 후 Office 로 열어 COM 편집' 한다(세 앱).
        // 기존 template 시스템 재활용 — PPT 디자인 A~D×레이아웃 / Word 보고서·경위서·제안서 /
        // Excel 지출결의서·거래명세서·재고관리표. 자유 편집은 여전히 열린 문서(COM).
        list.Add(new DocxCreateTool());
        list.Add(new XlsxCreateTool());
        list.Add(new PptxCreateTool());
        list.Add(new OfficeDocInspectTool());
        list.Add(new ChunkBuildTool());
        list.Add(new ChunkFetchTool());
        list.Add(new ChunkSearchTool());

        // COM Office 편집 툴('열려있는 문서 편집') — Desktop 전용. Windows·Office 없으면 빈 목록.
        list.AddRange(MoaiCode.Tools.Office.OfficeTools.CreateIfAvailable());

        // 스킬(마스터 스위치 on 일 때만) — 우선순위: 사용자 > 팀 공유. 오버헤드 최소화:
        // GUI 는 팀 스킬을 재동기화하지 않고 이미 받아둔 ~/.moai/team-skills 를 로드만 한다
        // (동기화는 CLI/로그인 플로우 담당). 실패는 non-fatal.
        // NOTE(핸드오프): 기본 번들 스킬(BundledSkills)은 CLI 어셈블리의 임베드 zip 에 묶여 있어
        //   GUI 에서 아직 제외. 공유 어셈블리로 옮기면 GUI 에도 추가 예정.
        if (GuiSettings.Load().SkillsEnabled && CollectSkillTool() is { } skillTool)
        {
            list.Add(skillTool);
        }

        return list;
    }

    // 스킬을 모아 SkillTool 로. 스킬이 없으면 null(호출부에서 걸러짐).
    private static ITool? CollectSkillTool()
    {
        try
        {
            var cwd = Workspace;
            var skills = new List<Skill>(SkillLoader.Discover(cwd));
            skills.AddRange(PluginLoader.Discover(cwd));
            var have = new HashSet<string>(skills.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);

            // 팀 공유 스킬(이미 동기화된 디렉터리 로드).
            foreach (var s in SkillLoader.LoadFromDir(MoaiCode.Cli.TeamSkills.Dir))
            {
                if (have.Add(s.Name))
                {
                    skills.Add(s);
                }
            }

            // CLI 개별 스킬 토글(~/.moai/skills-disabled.json)을 존중 — 사용자가 끈 스킬은 GUI 에서도 제외.
            var disabled = SkillState.LoadDisabled();
            var active = skills.Where(s => !disabled.Contains(s.Name)).ToList();

            return active.Count > 0 ? new SkillTool(active) : null;
        }
        catch
        {
            return null; // 스킬 수집 실패는 non-fatal — 앱 기동을 막지 않는다.
        }
    }

    /// <summary>OPENAI_BASE_URL / OPENAI_API_KEY 등 환경변수를 설정에서 채운다(멱등). 엔진 빌드 전에
    /// 도는 백그라운드 동기화 등도 자격증명을 스스로 확보할 수 있게 공개한다. 이미 있으면 덮지 않는다.</summary>
    public static void EnsureEnvReady()
    {
        try
        {
            ApplySettingsToEnv(SettingsLoader.Load(Workspace));
            ResolveCredentials();
        }
        catch
        {
            // 자격증명 확보 실패는 non-fatal — 호출부가 미설정을 감지해 처리한다.
        }
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

    private static PromptContext BuildPromptContext(string ws, Settings settings, IReadOnlyList<ITool> tools)
    {
        var platform = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsMacOS() ? "macos"
            : OperatingSystem.IsLinux() ? "linux"
            : "unknown";

        var model = settings.Model
            ?? Environment.GetEnvironmentVariable("MOAI_MODEL")
            ?? Environment.GetEnvironmentVariable("OPENAI_MODEL");

        return new PromptContext
        {
            WorkingDirectory = ws,
            IsGitRepo = false,
            Platform = platform,
            OsVersion = RuntimeInformation.OSDescription,
            CurrentDate = DateTimeOffset.Now.ToString("yyyy-MM-dd"),
            ModelDescription = string.IsNullOrEmpty(model) ? null : $"You are powered by the model {model}.",
            OutputStyle = settings.OutputStyle,
            ToolNames = tools.Select(t => t.Name).ToList(),
        };
    }
}
