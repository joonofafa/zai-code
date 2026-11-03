using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using MoaiCode.Config;
using MoaiCode.Core;
using MoaiCode.Core.Agent.Prompts;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;
using MoaiCode.Mcp.Skills;
using MoaiCode.Tools.Media;
using MoaiCode.Tools.Web;
using Xunit;

namespace MoaiCode.Core.Tests;

/// <summary>오프라인 모드(망분리 사내망)와, 인터넷 없이 동작해야 하는 번들 스킬.</summary>
[Collection("EnvMutating")]
public sealed class OfflineModeTests : IDisposable
{
    public void Dispose()
    {
        NetworkPolicy.Offline = false;
        ActiveProvider.Reset();
    }

    [Fact]
    public void Offline_is_read_from_the_user_layer_and_env_but_not_from_a_project()
    {
        Assert.False(Settings.Default.Offline);
        Assert.True(SettingsLoader.ApplyJson(Settings.Default, """{"offline": true}""").Offline);

        // 저장소(프로젝트 레이어)가 운영 정책을 바꾸지 못한다.
        var user = SettingsLoader.ApplyJson(Settings.Default, """{"offline": true}""");
        var project = SettingsLoader.ApplyJson(user, """{"offline": false}""",
            allowSensitive: false, allowAutomation: false, allowPermissionRules: false);
        Assert.True(project.Offline);

        Environment.SetEnvironmentVariable("MOAI_OFFLINE", "1");
        try
        {
            Assert.True(SettingsLoader.ApplyEnv(Settings.Default).Offline);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MOAI_OFFLINE", null);
        }
    }

    [Fact]
    public async Task Offline_hides_and_refuses_every_internet_tool()
    {
        ActiveProvider.Current = new ProviderProfile(ProviderKind.Zai, "https://api.z.ai/api/coding/paas/v4", "k", "glm", "glm-v");
        ITool[] net = [new WebFetchTool(), new WebSearchTool(), new ImageFetchTool(), new ImageCreateTool()];
        Assert.All(net, t => Assert.True(((IConditionalTool)t).IsAvailable));

        NetworkPolicy.Offline = true;
        Assert.All(net, t => Assert.False(((IConditionalTool)t).IsAvailable));

        var ctx = new ToolContext(Path.GetTempPath(), PermissionMode.Auto);
        Assert.Equal(L10n.Get("tools.offline.disabled", "WebFetch"), await Run(net[0], """{"url":"https://example.com/"}""", ctx));
        Assert.Equal(L10n.Get("tools.offline.disabled", "WebSearch"), await Run(net[1], """{"query":"x"}""", ctx));
        Assert.Equal(L10n.Get("tools.offline.disabled", "ImageFetch"), await Run(net[2], """{"url":"https://example.com/a.png","path":"a.png"}""", ctx));
        Assert.Equal(L10n.Get("tools.offline.disabled", "ImageCreate"), await Run(net[3], """{"prompt":"x","path":"a.png"}""", ctx));

        // 이미지 분석은 사내 비전 모델로 계속 쓰되, URL 내려받기만 막는다.
        Assert.True(new ImageAnalysisTool().IsAvailable);
        var vision = await Run(new ImageAnalysisTool(), """{"image_source":"https://example.com/a.png","prompt":"x"}""", ctx);
        Assert.Contains(L10n.Get("tools.offline.imageUrl"), vision);
    }

    [Fact]
    public void Offline_prompt_says_there_is_no_internet()
    {
        var on = SystemPromptBuilder.Build(new PromptContext { WorkingDirectory = "/w", Offline = true });
        var off = SystemPromptBuilder.Build(new PromptContext { WorkingDirectory = "/w" });
        Assert.Contains("Network: OFFLINE", on);
        Assert.DoesNotContain("Network: OFFLINE", off);
    }

    [Fact]
    public void Folder_skills_tell_the_model_where_their_files_are()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mate-skill-" + Guid.NewGuid().ToString("n"), "demo");
        var folder = new Skill("demo", "d", "Read references/a.md.", Path.Combine(dir, "SKILL.md"));
        var single = new Skill("one", "d", "Body.", Path.Combine(dir, "one.md"));

        var text = SkillTool.BodyWithBaseDirectory(folder);
        Assert.StartsWith($"Base directory for this skill: {dir}\n", text);
        Assert.EndsWith("Read references/a.md.", text);
        Assert.Equal("Body.", SkillTool.BodyWithBaseDirectory(single));
    }

    // 사내 인프라 스킬은 망분리 환경에서 쓰인다 — 인터넷 주소나 내려받기 지시가 있으면 안 되고,
    // 본문이 가리키는 references/·scripts/ 파일이 번들 안에 실제로 있어야 한다.
    public static readonly string[] OfflineSkills =
    [
        "man-page-lookup", "oracle-to-postgresql", "postgresql-diagnostics", "openshift-diagnostics",
        "k8s-manifest-review", "vllm-serving", "financial-secure-code-review", "external-code-intake",
        "change-and-incident-runbook",
    ];

    [Fact]
    public void Bundled_offline_skills_have_no_internet_links_and_their_references_exist()
    {
        using var stream = typeof(MoaiCode.Cli.BundledSkills).Assembly.GetManifestResourceStream("bundled-skills.zip");
        Assert.NotNull(stream);
        using var zip = new ZipArchive(stream!, ZipArchiveMode.Read);
        var entries = zip.Entries.Where(e => e.Length > 0).ToDictionary(e => e.FullName, e => e);
        var url = new Regex(@"https?://[A-Za-z0-9-]+\.[A-Za-z]", RegexOptions.IgnoreCase);
        var reference = new Regex(@"\b(references|scripts)/[\w.-]+\.(md|py|sh)\b");

        foreach (var name in OfflineSkills)
        {
            Assert.True(entries.ContainsKey($"{name}/SKILL.md"), $"{name} missing from the bundle");
            var tmp = Write(entries[$"{name}/SKILL.md"]);
            var skill = SkillLoader.LoadFile(tmp, name)!;
            File.Delete(tmp);
            Assert.Equal(name, skill.Name);
            Assert.False(string.IsNullOrWhiteSpace(skill.Description));
            Assert.StartsWith("MATE Builder original", skill.License);

            foreach (var (path, entry) in entries.Where(e => e.Key.StartsWith(name + "/", StringComparison.Ordinal)))
            {
                using var reader = new StreamReader(entry.Open());
                var text = reader.ReadToEnd();
                Assert.False(url.IsMatch(text), $"{path} links to the internet: {url.Match(text).Value}");
                foreach (Match m in reference.Matches(text))
                {
                    Assert.True(entries.ContainsKey($"{name}/{m.Value}"), $"{path} points to missing {m.Value}");
                }
            }
        }
    }

    private static string Write(ZipArchiveEntry entry)
    {
        var path = Path.Combine(Path.GetTempPath(), "mate-skill-" + Guid.NewGuid().ToString("n") + ".md");
        entry.ExtractToFile(path);
        return path;
    }

    private static async Task<string> Run(ITool tool, string json, ToolContext ctx)
    {
        using var doc = JsonDocument.Parse(json);
        var text = "";
        await foreach (var p in tool.ExecuteAsync(doc.RootElement, ctx, default))
        {
            if (p is ToolOutput o)
            {
                text += o.Text;
            }
        }

        return text;
    }
}
