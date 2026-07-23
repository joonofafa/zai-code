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
        list.Add(new DocxCreateTool());
        list.Add(new XlsxCreateTool());
        list.Add(new PptxCreateTool());
        list.Add(new OfficeDocInspectTool());
        list.Add(new ChunkBuildTool());
        list.Add(new ChunkFetchTool());
        list.Add(new ChunkSearchTool());

        // COM Office 편집 툴('열려있는 문서 편집') — Desktop 전용. Windows·Office 없으면 빈 목록.
        list.AddRange(MoaiCode.Tools.Office.OfficeTools.CreateIfAvailable());
        return list;
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
