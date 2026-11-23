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

// 1.4.0 리브랜딩(Z.ai Code → MATE Builder): 데이터 루트 ~/.zaicode → ~/.mate. 설정을 읽기 전에 옮긴다.
if (MoaiCode.Core.AppPaths.MigrateLegacyHome())
{
    MoaiLog.Info("data root moved from ~/.zaicode to ~/.mate");
}

// 도움말을 만들기 전에 언어를 확정해야 System.CommandLine 설명도 선택 언어로 생성된다.
var startupSettings = SettingsLoader.Load(Directory.GetCurrentDirectory());
L10n.SetLanguage(startupSettings.Language);

// 사내망 프록시 적용 (모든 HttpClient 생성보다 먼저 — DefaultProxy 세팅).
ProxyConfig.Apply(Directory.GetCurrentDirectory());

var root = new RootCommand(L10n.Get("app.description"));

// 헤드리스 1회 실행 공용 로직 (run 서브커맨드 / 루트의 -p 플래그가 공유).
static async Task<int> RunHeadlessAsync(string prompt, string? model, string outputFormat, bool ignoreConfirm, string? appendSystemPrompt, CancellationToken ct)
{
    var rt = await AppBootstrap.BuildAsync(interactive: false, verbose: false, streamJsonPermissions: false, ct, modelOverride: model,
        ignoreConfirm: ignoreConfirm, appendSystemPrompt: appendSystemPrompt);
    await using var _ = rt.Mcp;
    rt.Ctx.State.LastUserRequest = prompt;   // 위험 판정 분류기용 원문 요청
    return string.Equals(outputFormat, "stream-json", StringComparison.OrdinalIgnoreCase)
        ? await StreamJsonRunner.RunOnceAsync(rt.Ctx.Engine, prompt, ct)
        : await HeadlessRunner.RunAsync(rt.Ctx.Engine, prompt, ct, outputFormat);
}

// stdin 의 NDJSON 턴을 EOF 까지 처리하는 영속 모드 (--input-format stream-json).
static async Task<int> RunStreamLoopAsync(string? model, bool ignoreConfirm, string? appendSystemPrompt, string? resumeId, CancellationToken ct)
{
    var rt = await AppBootstrap.BuildAsync(interactive: false, verbose: false, streamJsonPermissions: true, ct, modelOverride: model,
        ignoreConfirm: ignoreConfirm, appendSystemPrompt: appendSystemPrompt);
    await using var _ = rt.Mcp;
    return await StreamJsonRunner.RunLoopAsync(rt.Ctx.Engine, rt.PermissionGate, ct, rt.Ctx.Sessions, resumeId);
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
// 배치 자동화: 사람의 확인 없이 실행(파괴적 명령 포함). 하드 차단·deny 규칙은 그대로, 확인 없이 지나간 동작은 모두 감사 로그에 남는다.
// 대화형 REPL 에서는 쓰지 않는다(사람이 있으니 확인한다).
static Option<bool> IgnoreConfirmOption() => new("--ignore-confirm") { Description = L10n.Get("cli.ignoreConfirm.description") };
// 시스템 프롬프트 끝에 덧붙일 문구(claude CLI 의 --append-system-prompt 와 같은 이름·동작).
// 값이 있으면 조립된 시스템 프롬프트 맨 끝에 빈 줄 하나를 두고 그대로 붙인다. 세션에는 저장하지 않는다(매 실행 인자로 받는다).
static Option<string?> AppendSystemPromptOption() => new("--append-system-prompt") { Description = L10n.Get("cli.appendSystemPrompt.description") };
var ignoreConfirmOpt = IgnoreConfirmOption();
var appendSystemPromptOpt = AppendSystemPromptOption();
var runCmd = new Command("run", L10n.Get("cli.run.description"));
runCmd.Arguments.Add(promptArg);
runCmd.Options.Add(modelOpt);
runCmd.Options.Add(outputFormatOpt);
runCmd.Options.Add(ignoreConfirmOpt);
runCmd.Options.Add(appendSystemPromptOpt);
runCmd.SetAction(async (ParseResult pr, CancellationToken ct) =>
    await RunHeadlessAsync(pr.GetValue(promptArg) ?? "", pr.GetValue(modelOpt), pr.GetValue(outputFormatOpt) ?? "text",
        pr.GetValue(ignoreConfirmOpt), pr.GetValue(appendSystemPromptOpt), ct));
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
// 툴 허용은 이 CLI 에선 권한 게이트/MOAI_YES 가 담당한다. --resume 은 stream-json 입력 모드에서만 세션을 복원한다.
var rootVerboseOpt = new Option<bool>("--verbose") { Description = "Accepted for CLI compatibility (no-op)." };
var rootAllowedToolsOpt = new Option<string?>("--allowedTools")
{
    Description = "Accepted for CLI compatibility (no-op) — use the permission gate or MOAI_YES=1.",
};
var rootResumeOpt = new Option<string?>("--resume")
{
    Description = "Session id to continue (stream-json input mode; ids come from `mate sessions`). Ignored elsewhere.",
};
root.Options.Add(printOpt);
root.Options.Add(rootModelOpt);
root.Options.Add(rootOutputFormatOpt);
root.Options.Add(rootInputFormatOpt);
root.Options.Add(rootVerboseOpt);
root.Options.Add(rootAllowedToolsOpt);
root.Options.Add(rootResumeOpt);
var rootIgnoreConfirmOpt = IgnoreConfirmOption();
root.Options.Add(rootIgnoreConfirmOpt);
var rootAppendSystemPromptOpt = AppendSystemPromptOption();
root.Options.Add(rootAppendSystemPromptOpt);

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

// sessions: 현재 폴더의 저장된 세션 목록(최근 순). 한 줄 = id \t 수정시각(ISO) \t 메시지 수 \t 제목.
// 런타임을 만들지 않는다 — 브리지·웹 UI 가 자주 부르므로 MCP 기동 없이 바로 끝나야 한다.
var sessionsCmd = new Command("sessions", L10n.Get("cli.sessions.description"));
sessionsCmd.SetAction(async (ParseResult pr, CancellationToken ct) =>
{
    var store = new MoaiCode.Persistence.SessionStore(MoaiCode.Core.Memory.ProjectMemory.SessionsDir(Directory.GetCurrentDirectory()));
    foreach (var s in await store.ListInfosAsync(ct))
    {
        var title = s.Title.ReplaceLineEndings(" ").Replace('\t', ' ');
        Console.WriteLine($"{s.Id}\t{s.ModifiedAt:yyyy-MM-ddTHH:mm:ss}\t{s.MessageCount}\t{title}");
    }

    return 0;
});
root.Subcommands.Add(sessionsCmd);
root.Subcommands.Add(ProvidersCommand.Build());

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

// audit verify: 감사 로그의 해시 이어짐 확인(중간 줄 수정·삭제·삽입 탐지). 맞으면 0, 어긋나면 1.
var auditCmd = new Command("audit", L10n.Get("cli.audit.description"));
var auditVerifyCmd = new Command("verify", L10n.Get("cli.audit.verify.description"));
var auditFileOpt = new Option<string?>("--file") { Description = L10n.Get("cli.audit.verify.file") };
auditVerifyCmd.Options.Add(auditFileOpt);
auditVerifyCmd.SetAction((ParseResult pr, CancellationToken ct) =>
{
    var path = pr.GetValue(auditFileOpt) ?? AuditLog.FilePath;
    var r = AuditLog.Verify(path);
    if (r.BadLine is { } bad)
    {
        Console.WriteLine(L10n.Get("cli.audit.verify.broken", path, bad));
        return Task.FromResult(1);
    }

    Console.WriteLine(L10n.Get("cli.audit.verify.ok", path, r.Lines, r.Chained, r.Head ?? "-"));
    if (r.Lines > r.Chained)
    {
        Console.WriteLine(L10n.Get("cli.audit.verify.legacy", r.Lines - r.Chained));
    }

    if (!r.Anchored)
    {
        Console.WriteLine(L10n.Get("cli.audit.verify.trimmed"));
    }

    return Task.FromResult(0);
});
auditCmd.Subcommands.Add(auditVerifyCmd);
root.Subcommands.Add(auditCmd);

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
        return await RunStreamLoopAsync(pr.GetValue(rootModelOpt), pr.GetValue(rootIgnoreConfirmOpt), pr.GetValue(rootAppendSystemPromptOpt), pr.GetValue(rootResumeOpt), ct);
    }

    var printPrompt = pr.GetValue(printOpt);
    // `cat prompt | mate -p --output-format json`: 값 없는 -p 는 stdin 을 프롬프트로 읽는다(claude CLI 와 같음).
    // 예전엔 REPL 로 빠져 배너가 stdout 에 찍히고 JSON 이 나오지 않은 채 종료 코드 0 이었다.
    if (string.IsNullOrEmpty(printPrompt) && pr.GetResult(printOpt) is not null)
    {
        printPrompt = Console.IsInputRedirected ? (await Console.In.ReadToEndAsync(ct)).Trim() : "";
        if (printPrompt.Length == 0)
        {
            Console.Error.WriteLine(L10n.Get("cli.print.noPrompt"));
            return 1;
        }
    }

    if (!string.IsNullOrEmpty(printPrompt))
    {
        return await RunHeadlessAsync(printPrompt, pr.GetValue(rootModelOpt), pr.GetValue(rootOutputFormatOpt) ?? "text",
            pr.GetValue(rootIgnoreConfirmOpt), pr.GetValue(rootAppendSystemPromptOpt), ct);
    }

    var interactive = !Console.IsInputRedirected;

    var rt = await AppBootstrap.BuildAsync(
        interactive: interactive, verbose: true, streamJsonPermissions: false, ct, modelOverride: pr.GetValue(rootModelOpt),
        ignoreConfirm: pr.GetValue(rootIgnoreConfirmOpt));
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
