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

// run: 1회 프롬프트 헤드리스 실행
var promptArg = new Argument<string>("prompt") { Description = L10n.Get("cli.prompt.description") };
var modelOpt = new Option<string?>("--model", "-m") { Description = L10n.Get("cli.model.description") };
var runCmd = new Command("run", L10n.Get("cli.run.description"));
runCmd.Arguments.Add(promptArg);
runCmd.Options.Add(modelOpt);
runCmd.SetAction(async (ParseResult pr, CancellationToken ct) =>
{
    var prompt = pr.GetValue(promptArg) ?? "";
    var m = pr.GetValue(modelOpt);
    if (!string.IsNullOrEmpty(m))
    {
        Environment.SetEnvironmentVariable("MOAI_MODEL", m);
    }

    var rt = await AppBootstrap.BuildAsync(interactive: false, verbose: false, ct);
    await using var _ = rt.Mcp;
    rt.Ctx.State.LastUserRequest = prompt;   // 위험 판정 분류기용 원문 요청
    return await HeadlessRunner.RunAsync(rt.Ctx.Engine, prompt, ct);
});
root.Subcommands.Add(runCmd);

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

// login / logout: open-moai 계정 로그인 (목표 UX — 설정 제로)
var hostOpt = new Option<string?>("--host") { Description = L10n.Get("cli.host.description") };
var loginCmd = new Command("login", L10n.Get("cli.login.description"));
loginCmd.Options.Add(hostOpt);
loginCmd.SetAction(async (ParseResult pr, CancellationToken ct) =>
    await LoginFlow.RunAsync(pr.GetValue(hostOpt) ?? LoginFlow.ResolveDefaultHost(), ct) ? 0 : 1);
root.Subcommands.Add(loginCmd);

var logoutCmd = new Command("logout", L10n.Get("cli.logout.description"));
logoutCmd.SetAction((ParseResult pr, CancellationToken ct) =>
{
    LoginFlow.Logout();
    return Task.FromResult(0);
});
root.Subcommands.Add(logoutCmd);

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

// 기본 동작(서브커맨드 없음): 대화형 REPL
root.SetAction(async (ParseResult pr, CancellationToken ct) =>
{
    var interactive = !Console.IsInputRedirected;

    // 첫 실행/미인증 시 자동 로그인 (엔터프라이즈: 열면 바로 로그인 화면)
    if (interactive && !LoginFlow.HasCredential())
    {
        await LoginFlow.RunAsync(LoginFlow.ResolveDefaultHost(), ct);
    }

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
