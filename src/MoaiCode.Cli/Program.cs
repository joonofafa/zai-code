using System.CommandLine;
using MoaiCode.Cli;
using MoaiCode.Config;
using MoaiCode.Localization;
using MoaiCode.Tui;
using MoaiCode.Tui.Commands;

// 엔트리포인트 (TS의 bin/openclaude + src/entrypoints/cli.tsx 대응).
// System.CommandLine 기반 서브커맨드 디스패치. 인자 없으면 대화형 REPL.

// Windows 콘솔을 UTF-8 로 (기본 CP949 에서 ❯·✓·• 가 '?' 로 깨지는 문제). 어떤 출력보다 먼저.
ConsoleSetup.EnsureUtf8();

// 도움말을 만들기 전에 언어를 확정해야 System.CommandLine 설명도 선택 언어로 생성된다.
var startupSettings = SettingsLoader.Load(Directory.GetCurrentDirectory());
L10n.SetLanguage(startupSettings.Language);

// 사내망 프록시 적용 (모든 HttpClient 생성보다 먼저 — DefaultProxy 세팅).
ProxyConfig.Apply(Directory.GetCurrentDirectory());

var root = new RootCommand(L10n.Get("app.description"));

// 헤드리스 1회 실행 공용 로직 (run 서브커맨드 / 루트의 -p 플래그가 공유).
static async Task<int> RunHeadlessAsync(string prompt, string? model, string outputFormat, CancellationToken ct)
{
    if (!string.IsNullOrEmpty(model))
    {
        Environment.SetEnvironmentVariable("MOAI_MODEL", model);
    }

    var rt = await AppBootstrap.BuildAsync(interactive: false, verbose: false, ct);
    await using var _ = rt.Mcp;
    rt.Ctx.State.LastUserRequest = prompt;   // 위험 판정 분류기용 원문 요청
    return string.Equals(outputFormat, "stream-json", StringComparison.OrdinalIgnoreCase)
        ? await StreamJsonRunner.RunOnceAsync(rt.Ctx.Engine, prompt, ct)
        : await HeadlessRunner.RunAsync(rt.Ctx.Engine, prompt, ct, outputFormat);
}

// stdin 의 NDJSON 턴을 EOF 까지 처리하는 영속 모드 (--input-format stream-json).
static async Task<int> RunStreamLoopAsync(string? model, CancellationToken ct)
{
    if (!string.IsNullOrEmpty(model))
    {
        Environment.SetEnvironmentVariable("MOAI_MODEL", model);
    }

    var rt = await AppBootstrap.BuildAsync(interactive: false, verbose: false, streamJsonPermissions: true, ct);
    await using var _ = rt.Mcp;
    return await StreamJsonRunner.RunLoopAsync(rt.Ctx.Engine, rt.PermissionGate, ct);
}

// run: 1회 프롬프트 헤드리스 실행
var promptArg = new Argument<string>("prompt") { Description = L10n.Get("cli.prompt.description") };
var modelOpt = new Option<string?>("--model", "-m") { Description = L10n.Get("cli.model.description") };
var outputFormatOpt = new Option<string>("--output-format")
{
    Description = "Output format: text (default) or json",
    DefaultValueFactory = _ => "text",
};
outputFormatOpt.AcceptOnlyFromAmong("text", "json", "stream-json");
var runCmd = new Command("run", L10n.Get("cli.run.description"));
runCmd.Arguments.Add(promptArg);
runCmd.Options.Add(modelOpt);
runCmd.Options.Add(outputFormatOpt);
runCmd.SetAction(async (ParseResult pr, CancellationToken ct) =>
    await RunHeadlessAsync(pr.GetValue(promptArg) ?? "", pr.GetValue(modelOpt), pr.GetValue(outputFormatOpt) ?? "text", ct));
root.Subcommands.Add(runCmd);

// 루트 레벨 -p/--print: `moai -p "프롬프트"` = 헤드리스 1회 실행(claude -p 패리티).
// 값 없이 `-p` 만 주는 사용법(= stdin 으로 턴을 흘려보내는 stream-json 모드)도 허용한다.
// ZeroOrOne 이 아니면 `-p --input-format ...` 에서 뒤 옵션을 값으로 삼켜버린다.
var printOpt = new Option<string?>("--print", "-p")
{
    Description = L10n.Get("cli.prompt.description"),
    Arity = ArgumentArity.ZeroOrOne,
};
var rootModelOpt = new Option<string?>("--model", "-m") { Description = L10n.Get("cli.model.description") };
var rootOutputFormatOpt = new Option<string>("--output-format")
{
    Description = "Output format: text (default), json, or stream-json",
    DefaultValueFactory = _ => "text",
};
rootOutputFormatOpt.AcceptOnlyFromAmong("text", "json", "stream-json");
// --input-format stream-json: stdin 의 NDJSON 턴을 EOF 까지 이어서 처리(영속 프로세스).
var rootInputFormatOpt = new Option<string>("--input-format")
{
    Description = "Input format: text (default) or stream-json (NDJSON turns on stdin)",
    DefaultValueFactory = _ => "text",
};
rootInputFormatOpt.AcceptOnlyFromAmong("text", "stream-json");
// claude CLI 호환을 위해 받기만 하는 옵션들 — 파싱이 깨지지 않게 한다.
// 툴 허용은 이 CLI 에선 권한 게이트/MOAI_YES 가 담당하고, 세션 복원은 아직 없다.
var rootVerboseOpt = new Option<bool>("--verbose") { Description = "Accepted for CLI compatibility (no-op)." };
var rootAllowedToolsOpt = new Option<string?>("--allowedTools")
{
    Description = "Accepted for CLI compatibility (no-op) — use the permission gate or MOAI_YES=1.",
};
var rootResumeOpt = new Option<string?>("--resume")
{
    Description = "Accepted for CLI compatibility (no-op) — session restore is not implemented.",
};
root.Options.Add(printOpt);
root.Options.Add(rootModelOpt);
root.Options.Add(rootOutputFormatOpt);
root.Options.Add(rootInputFormatOpt);
root.Options.Add(rootVerboseOpt);
root.Options.Add(rootAllowedToolsOpt);
root.Options.Add(rootResumeOpt);

// tools: 사용 가능한 툴 목록
var toolsCmd = new Command("tools", L10n.Get("cli.tools.description"));
toolsCmd.SetAction(async (ParseResult pr, CancellationToken ct) =>
{
    var rt = await AppBootstrap.BuildAsync(interactive: false, verbose: false, ct);
    await using var _ = rt.Mcp;
    foreach (var t in rt.Tools)
    {
        Console.WriteLine($"{t.Name}\t{t.Description}");
    }

    return 0;
});
root.Subcommands.Add(toolsCmd);

// skills: 로드된 스킬 목록
var skillsCmd = new Command("skills", L10n.Get("cli.skills.description"));
skillsCmd.SetAction(async (ParseResult pr, CancellationToken ct) =>
{
    var rt = await AppBootstrap.BuildAsync(interactive: false, verbose: false, ct);
    await using var _ = rt.Mcp;
    if (rt.SkillNames.Count == 0)
    {
        Console.WriteLine(L10n.Get("cli.skills.empty"));
    }

    foreach (var s in rt.SkillNames)
    {
        Console.WriteLine(s);
    }

    return 0;
});
root.Subcommands.Add(skillsCmd);

// mcp list: 설정된 MCP 서버
var mcpCmd = new Command("mcp", L10n.Get("cli.mcp.description"));
var mcpListCmd = new Command("list", L10n.Get("cli.mcp.list.description"));
mcpListCmd.SetAction((ParseResult pr, CancellationToken ct) =>
{
    var configs = MoaiCode.Mcp.McpConfigLoader.Discover(Directory.GetCurrentDirectory());
    if (configs.Count == 0)
    {
        Console.WriteLine(L10n.Get("cli.mcp.empty"));
    }

    foreach (var c in configs)
    {
        Console.WriteLine($"{c.Name}\t{c.Command} {string.Join(' ', c.Args)}");
    }

    return Task.FromResult(0);
});
mcpCmd.Subcommands.Add(mcpListCmd);
root.Subcommands.Add(mcpCmd);

// auth: 자격증명 저장/조회
var authCmd = new Command("auth", L10n.Get("cli.auth.description"));
var providerArg = new Argument<string>("provider") { Description = L10n.Get("cli.auth.provider.description") };
var keyArg = new Argument<string>("key") { Description = L10n.Get("cli.auth.key.description") };
var authSetCmd = new Command("set", L10n.Get("cli.auth.set.description"));
authSetCmd.Arguments.Add(providerArg);
authSetCmd.Arguments.Add(keyArg);
authSetCmd.SetAction((ParseResult pr, CancellationToken ct) =>
{
    var provider = pr.GetValue(providerArg) ?? "";
    var key = pr.GetValue(keyArg) ?? "";
    var name = $"{provider.ToUpperInvariant()}_API_KEY";
    new FileCredentialStore().Set(name, key);
    Console.WriteLine(L10n.Get("cli.auth.saved", name));
    return Task.FromResult(0);
});
var authListCmd = new Command("list", L10n.Get("cli.auth.list.description"));
authListCmd.SetAction((ParseResult pr, CancellationToken ct) =>
{
    var keys = new FileCredentialStore().Keys();
    Console.WriteLine(keys.Count == 0 ? L10n.Get("cli.empty") : string.Join("\n", keys));
    return Task.FromResult(0);
});
authCmd.Subcommands.Add(authSetCmd);
authCmd.Subcommands.Add(authListCmd);
root.Subcommands.Add(authCmd);

// proxy: 사내망 HTTP(S) 프록시 설정 (서버 필수, id/pw 선택)
var proxyUrlArg = new Argument<string?>("url") { Description = L10n.Get("cli.proxy.url.description"), Arity = ArgumentArity.ZeroOrOne };
var proxyUserOpt = new Option<string?>("--user", "-u") { Description = L10n.Get("cli.proxy.user.description") };
var proxyClearOpt = new Option<bool>("--clear") { Description = L10n.Get("cli.proxy.clear.description") };
var proxyCmd = new Command("proxy", L10n.Get("cli.proxy.description"));
proxyCmd.Arguments.Add(proxyUrlArg);
proxyCmd.Options.Add(proxyUserOpt);
proxyCmd.Options.Add(proxyClearOpt);
proxyCmd.SetAction((ParseResult pr, CancellationToken ct) =>
{
    if (pr.GetValue(proxyClearOpt))
    {
        ProxyFlow.Clear();
        return Task.FromResult(0);
    }

    var url = pr.GetValue(proxyUrlArg);
    if (string.IsNullOrWhiteSpace(url))
    {
        return Task.FromResult(ProxyFlow.RunInteractive() ? 0 : 1);
    }

    var user = pr.GetValue(proxyUserOpt);
    var pw = string.IsNullOrEmpty(user) ? null : MoaiCode.Tui.PasswordPrompt.Read(L10n.Get("cli.proxy.password"));
    ProxyFlow.Set(url!, string.IsNullOrEmpty(user) ? null : user, pw);
    return Task.FromResult(0);
});
root.Subcommands.Add(proxyCmd);

// language: UI 언어 조회/변경. 저장 후 다음 실행의 도움말부터 반영된다.
var languageArg = new Argument<string?>("code")
{
    Description = L10n.Get("cli.language.code.description"),
    Arity = ArgumentArity.ZeroOrOne,
};
var languageCmd = new Command("language", L10n.Get("cli.language.description"));
languageCmd.Arguments.Add(languageArg);
languageCmd.SetAction((ParseResult pr, CancellationToken ct) =>
{
    var requested = pr.GetValue(languageArg);
    if (string.IsNullOrWhiteSpace(requested))
    {
        var current = L10n.CurrentLanguage;
        var available = string.Join(", ", L10n.SupportedLanguages.Select(x => $"{x.Code} ({x.DisplayName})"));
        Console.WriteLine(L10n.Get("slash.language.current", current, L10n.GetLanguageDisplayName(current)));
        Console.WriteLine(L10n.Get("slash.language.available", available));
        return Task.FromResult(0);
    }

    var language = L10n.NormalizeLanguage(requested);
    if (language is null)
    {
        Console.Error.WriteLine(L10n.Get("slash.language.unsupported", requested));
        Console.Error.WriteLine(L10n.Get("slash.language.usage"));
        return Task.FromResult(1);
    }

    L10n.SetLanguage(language);
    SettingsWriter.Set(new Dictionary<string, string?> { ["language"] = language });
    Environment.SetEnvironmentVariable("MOAI_LANGUAGE", language);
    Console.WriteLine(L10n.Get("slash.language.changed", language, L10n.GetLanguageDisplayName(language)));
    return Task.FromResult(0);
});
root.Subcommands.Add(languageCmd);

// 기본 동작: -p/--print 가 있으면 헤드리스 1회 실행, 없으면 대화형 REPL.
root.SetAction(async (ParseResult pr, CancellationToken ct) =>
{
    // --input-format stream-json: 프롬프트를 인자로 받지 않고 stdin 의 NDJSON 턴을 계속 처리한다.
    // (브리지는 `-p --input-format stream-json --output-format stream-json` 처럼 -p 를 값 없이 준다.)
    if (string.Equals(pr.GetValue(rootInputFormatOpt), "stream-json", StringComparison.OrdinalIgnoreCase))
    {
        return await RunStreamLoopAsync(pr.GetValue(rootModelOpt), ct);
    }

    var printPrompt = pr.GetValue(printOpt);
    if (!string.IsNullOrEmpty(printPrompt))
    {
        return await RunHeadlessAsync(printPrompt, pr.GetValue(rootModelOpt), pr.GetValue(rootOutputFormatOpt) ?? "text", ct);
    }

    var interactive = !Console.IsInputRedirected;

    var rt = await AppBootstrap.BuildAsync(
        interactive: interactive, verbose: true, ct);
    await using var _ = rt.Mcp;

    var app = new ReplApp(rt.Ctx, SlashRegistry.CreateDefault());
    try
    {
        await app.RunAsync(ct);
    }
    catch (OperationCanceledException)
    {
        // Ctrl+C 정상 종료
    }

    return 0;
});

return await root.Parse(args).InvokeAsync();
