using System.CommandLine;
using MoaiCode.Cli;
using MoaiCode.Config;
using MoaiCode.Tui;
using MoaiCode.Tui.Commands;

// 엔트리포인트 (TS의 bin/openclaude + src/entrypoints/cli.tsx 대응).
// System.CommandLine 기반 서브커맨드 디스패치. 인자 없으면 대화형 REPL.

// Windows 콘솔을 UTF-8 로 (기본 CP949 에서 ❯·✓·• 가 '?' 로 깨지는 문제). 어떤 출력보다 먼저.
ConsoleSetup.EnsureUtf8();

// 사내망 프록시 적용 (모든 HttpClient 생성보다 먼저 — DefaultProxy 세팅).
ProxyConfig.Apply(Directory.GetCurrentDirectory());

var root = new RootCommand("MoAI Code — open coding-agent CLI");

// run: 1회 프롬프트 헤드리스 실행
var promptArg = new Argument<string>("prompt") { Description = "실행할 프롬프트" };
var modelOpt = new Option<string?>("--model", "-m") { Description = "모델 id 오버라이드" };
var runCmd = new Command("run", "프롬프트를 한 번 실행하고 종료 (스크립트/CI용)");
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
var toolsCmd = new Command("tools", "사용 가능한 툴 목록");
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
var skillsCmd = new Command("skills", "로드된 스킬 목록");
skillsCmd.SetAction(async (ParseResult pr, CancellationToken ct) =>
{
    var rt = await AppBootstrap.BuildAsync(interactive: false, verbose: false, ct);
    await using var _ = rt.Mcp;
    if (rt.SkillNames.Count == 0)
    {
        Console.WriteLine("(스킬 없음)");
    }

    foreach (var s in rt.SkillNames)
    {
        Console.WriteLine(s);
    }

    return 0;
});
root.Subcommands.Add(skillsCmd);

// mcp list: 설정된 MCP 서버
var mcpCmd = new Command("mcp", "MCP 서버 관리");
var mcpListCmd = new Command("list", "설정된 MCP 서버 목록");
mcpListCmd.SetAction((ParseResult pr, CancellationToken ct) =>
{
    var configs = MoaiCode.Mcp.McpConfigLoader.Discover(Directory.GetCurrentDirectory());
    if (configs.Count == 0)
    {
        Console.WriteLine("(.mcp.json 없음)");
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
var authCmd = new Command("auth", "자격증명 관리");
var providerArg = new Argument<string>("provider") { Description = "예: openai, anthropic" };
var keyArg = new Argument<string>("key") { Description = "API 키" };
var authSetCmd = new Command("set", "자격증명 저장: auth set <provider> <key>");
authSetCmd.Arguments.Add(providerArg);
authSetCmd.Arguments.Add(keyArg);
authSetCmd.SetAction((ParseResult pr, CancellationToken ct) =>
{
    var provider = pr.GetValue(providerArg) ?? "";
    var key = pr.GetValue(keyArg) ?? "";
    var name = $"{provider.ToUpperInvariant()}_API_KEY";
    new FileCredentialStore().Set(name, key);
    Console.WriteLine($"저장됨: {name}");
    return Task.FromResult(0);
});
var authListCmd = new Command("list", "저장된 자격증명 키 목록");
authListCmd.SetAction((ParseResult pr, CancellationToken ct) =>
{
    var keys = new FileCredentialStore().Keys();
    Console.WriteLine(keys.Count == 0 ? "(없음)" : string.Join("\n", keys));
    return Task.FromResult(0);
});
authCmd.Subcommands.Add(authSetCmd);
authCmd.Subcommands.Add(authListCmd);
root.Subcommands.Add(authCmd);

// login / logout: open-moai 계정 로그인 (목표 UX — 설정 제로)
var hostOpt = new Option<string?>("--host") { Description = "open-moai 호스트 (env MOAI_LOGIN_HOST > 설정 host > 컴파일 기본값)" };
var loginCmd = new Command("login", "open-moai 계정으로 로그인 (이메일/비번/MFA → 모델 선택)");
loginCmd.Options.Add(hostOpt);
loginCmd.SetAction(async (ParseResult pr, CancellationToken ct) =>
    await LoginFlow.RunAsync(pr.GetValue(hostOpt) ?? LoginFlow.ResolveDefaultHost(), ct) ? 0 : 1);
root.Subcommands.Add(loginCmd);

var logoutCmd = new Command("logout", "저장된 로그인(키) 제거");
logoutCmd.SetAction((ParseResult pr, CancellationToken ct) =>
{
    LoginFlow.Logout();
    return Task.FromResult(0);
});
root.Subcommands.Add(logoutCmd);

// proxy: 사내망 HTTP(S) 프록시 설정 (서버 필수, id/pw 선택)
var proxyUrlArg = new Argument<string?>("url") { Description = "프록시 서버 URL (예: http://proxy.corp:8080)", Arity = ArgumentArity.ZeroOrOne };
var proxyUserOpt = new Option<string?>("--user", "-u") { Description = "프록시 인증 사용자 (선택)" };
var proxyClearOpt = new Option<bool>("--clear") { Description = "프록시 설정 해제" };
var proxyCmd = new Command("proxy", "사내망 프록시 설정 (인자 없으면 대화형)");
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
    var pw = string.IsNullOrEmpty(user) ? null : MoaiCode.Tui.PasswordPrompt.Read("프록시 비밀번호: ");
    ProxyFlow.Set(url!, string.IsNullOrEmpty(user) ? null : user, pw);
    return Task.FromResult(0);
});
root.Subcommands.Add(proxyCmd);

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
