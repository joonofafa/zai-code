using System.Xml.Linq;
using MoaiCode.Core.Agent;
using MoaiCode.Core.Tools;
using MoaiCode.Persistence;
using MoaiCode.Providers;
using MoaiCode.Tui.Commands;
using Xunit;

namespace MoaiCode.Core.Tests;

/// <summary>/license 의 제3자 고지 표시와, 배포 패키지가 고지 문서에서 빠지지 않는지(드리프트 가드).</summary>
// 영어 문구를 단언한다 — /language 테스트가 전역 L10n 언어를 바꾸므로 같은 컬렉션으로 직렬화.
[Collection("EnvMutating")]
public class LicenseCommandTests
{
    private static SlashContext Context(IReadOnlyList<(string Name, string Source, string? License)> skills)
    {
        var dir = Path.Combine(Path.GetTempPath(), "occs-license-" + Guid.NewGuid().ToString("n"));
        return new SlashContext(
            new QueryEngine(new EchoChatModel(), Array.Empty<ITool>()),
            new SessionStore(dir),
            new HistoryStore(Path.Combine(dir, "history.jsonl")),
            new CheckpointStore(dir, Path.Combine(dir, "checkpoints")),
            new AgentRuntimeState(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            "EchoChatModel",
            GetSkillLicenses: () => skills);
    }

    private static readonly string Notices = ThirdPartyNotices.Text;

    [Fact]
    public void Notices_are_embedded_and_parsed()
    {
        var entries = ThirdPartyNotices.Entries(Notices);
        Assert.Contains(entries, e => e is { Kind: "library", Name: "PdfPig", Version: "0.1.15" } && e.License.Contains("Apache-2.0"));
        Assert.Contains(entries, e => e is { Kind: "library", Name: "Markdig" } && e.License == "BSD-2-Clause");
        Assert.Contains(entries, e => e.Kind == "library" && e.Name == "StbImageSharp" && e.License.StartsWith("MIT"));
        Assert.Contains(entries, e => e is { Kind: "theme", Name: "Zen Dark", Source: "Zenburn" });
        Assert.DoesNotContain(entries, e => e.Name.Contains("ImageSharp") && e.Name != "StbImageSharp");
    }

    [Theory]
    [InlineData("mit", "Permission is hereby granted")]
    [InlineData("bsd", "Redistribution and use in source and binary forms")]
    [InlineData("apache", "Version 2.0, January 2004")]
    public void License_texts_are_extracted(string key, string expected)
    {
        var text = ThirdPartyNotices.LicenseText(Notices, key);
        Assert.NotNull(text);
        Assert.Contains(expected, text);
        Assert.DoesNotContain("```", text);
        Assert.DoesNotContain("\n## ", text);   // 다음 절로 넘어가지 않는다
    }

    [Fact]
    public void Overview_lists_libraries_themes_and_skills()
    {
        var ctx = Context([("code-review", "bundled", null), ("mine", "user", "MIT")]);
        var text = LicenseCommand.Overview(ctx, Notices);
        Assert.Contains("PdfPig 0.1.15: Apache-2.0", text);
        Assert.Contains("CliWrap 3.7.1: MIT", text);
        Assert.Contains("Carbon Dark (from One Dark", text);
        Assert.Contains("code-review (bundled)", text);
        Assert.Contains("mine (user): MIT", text);
        Assert.Contains("/license <name|mit|bsd|apache>", text);
    }

    [Fact]
    public void Detail_shows_the_component_row_and_its_license_text()
    {
        var text = LicenseCommand.Detail("pdfpig", Notices);
        Assert.StartsWith("PdfPig 0.1.15: Apache-2.0", text);
        Assert.Contains("TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION", text);

        Assert.Contains("Redistribution and use", LicenseCommand.Detail("Markdig", Notices));
        Assert.Contains("no component matching 'nope'", LicenseCommand.Detail("nope", Notices));
    }

    // 배포본에 들어가는 패키지(Directory.Packages.props 중 테스트 전용 제외)는 모두 고지 표에 있어야 한다 —
    // 패키지를 추가하고 고지를 빠뜨리면 여기서 실패한다(예전엔 고지 문서가 배포본에 아예 없었다).
    [Fact]
    public void Every_shipped_package_is_listed_in_the_notices()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Packages.props")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var testOnly = new[] { "xunit", "xunit.runner.visualstudio", "Microsoft.NET.Test.Sdk" };
        var shipped = XDocument.Load(Path.Combine(dir!.FullName, "Directory.Packages.props"))
            .Descendants("PackageVersion")
            .Select(e => (Id: (string)e.Attribute("Include")!, Version: (string)e.Attribute("Version")!))
            .Where(p => !testOnly.Contains(p.Id))
            .ToList();
        Assert.NotEmpty(shipped);

        var libs = ThirdPartyNotices.Entries(Notices).Where(e => e.Kind == "library").ToList();
        foreach (var (id, version) in shipped)
        {
            Assert.True(
                libs.Any(l => l.Name.Split(',').Select(n => n.Trim()).Contains(id) && l.Version == version),
                $"{id} {version} is shipped but missing from THIRD_PARTY_NOTICES.md (or its version differs)");
        }
    }
}
