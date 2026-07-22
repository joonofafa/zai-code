using MoaiCode.Config;
using MoaiCode.Tui;
using Spectre.Console;

namespace MoaiCode.Cli;

/// <summary>
/// 대화형 로그인 흐름: 이메일/비번(+MFA) → open-moai 인증 → 사용자 키 수신 →
/// 자격증명/설정 저장 → 모델 선택. 목표 UX(설정 제로 B2B 로그인).
/// </summary>
public static class LoginFlow
{
    // 엔터프라이즈 배포 기본 호스트 (컴파일 시 최종 fallback).
    // CLI 인자(--host) 처리는 호출자(Program.cs) 책임이며, 이 함수는 인자가 없을 때
    // 사용할 값을 결정한다. 우선순위: (1) 환경변수 MOAI_LOGIN_HOST,
    // (2) 설정 파일의 host 키, (3) DefaultHost 상수.
    public const string DefaultHost = "https://vip.bccard.ai";

    /// <summary>
    /// 런타임에 실제 사용할 호스트 결정 (env → 설정 → 상수 순).
    /// CLI 인자로 명시된 host가 있다면 호출자가 그 값을 우선 사용해야 한다.
    /// </summary>
    public static string ResolveDefaultHost()
    {
        var env = Environment.GetEnvironmentVariable("MOAI_LOGIN_HOST");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env.Trim();
        }

        try
        {
            var s = SettingsLoader.Load(Directory.GetCurrentDirectory());
            if (!string.IsNullOrWhiteSpace(s.Host))
            {
                return s.Host!.Trim();
            }
        }
        catch
        {
            // best-effort: 설정 파싱 실패 시 상수 fallback.
        }

        return DefaultHost;
    }

    public static async Task<bool> RunAsync(string host, CancellationToken ct)
    {
        host = host.TrimEnd('/');
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[aqua]MoAI Code 로그인[/] [grey70]· {Markup.Escape(host)}[/]");

        Console.Write("email: ");
        var email = (Console.ReadLine() ?? string.Empty).Trim();
        if (email.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]취소됨[/]");
            return false;
        }

        var password = PasswordPrompt.Read("password: ");

        // 사내망 프록시 (로그인 요청도 프록시를 경유해야 하므로 로그인 전에 적용).
        MaybeConfigureProxy();

        var client = new OpenMoaiClient(host);
        var r = await client.LoginAsync(email, password, ct).ConfigureAwait(false);

        if (r.Status == "mfa_required" && !string.IsNullOrEmpty(r.MfaToken))
        {
            Console.Write("MFA 코드: ");
            var code = (Console.ReadLine() ?? string.Empty).Trim();
            r = await client.LoginMfaAsync(r.MfaToken, code, ct).ConfigureAwait(false);
        }

        if (r.Status != "ok" || string.IsNullOrEmpty(r.ApiKey))
        {
            AnsiConsole.MarkupLine($"[red]로그인 실패: {Markup.Escape(r.Error ?? "알 수 없는 오류")}[/]");
            return false;
        }

        // 키 저장 (비번은 메모리에서 사라짐 — 저장 안 함).
        new FileCredentialStore().Set("OPENAI_API_KEY", r.ApiKey);
        var baseUrl = string.IsNullOrEmpty(r.BaseUrl) ? host + "/api/v1" : r.BaseUrl;

        // 모델 선택.
        var model = r.DefaultModel;
        if (r.Models.Count > 0)
        {
            var defIdx = Math.Max(0, r.Models.ToList().FindIndex(m =>
                string.Equals(m, r.DefaultModel, StringComparison.OrdinalIgnoreCase)));
            var pick = SelectList.Prompt("사용할 모델을 선택하세요:", r.Models, defIdx);
            if (pick >= 0)
            {
                model = r.Models[pick];
            }
        }

        SettingsWriter.Set(new Dictionary<string, string?>
        {
            ["provider"] = "openai",
            ["host"] = host,
            ["baseUrl"] = baseUrl,
            ["model"] = model,
            ["account"] = email,
            ["loginAt"] = DateTimeOffset.Now.ToString("o"),
            ["orgName"] = r.OrgName,
        });

        AnsiConsole.MarkupLine($"[green]✓ 로그인 완료[/] [grey70]· {Markup.Escape(model ?? "(모델 미선택)")} · {Markup.Escape(host)}[/]");
        return true;
    }

    // 로그인 전 사내 프록시 설정을 물어보고 적용·저장한다.
    private static void MaybeConfigureProxy()
    {
        var existing = SettingsLoader.Load(Directory.GetCurrentDirectory()).ProxyUrl;
        var prompt = string.IsNullOrWhiteSpace(existing)
            ? "사내 프록시 설정이 필요합니까?"
            : $"사내 프록시가 이미 설정돼 있습니다 ({Markup.Escape(existing!)}). 변경할까요?";
        if (!AnsiConsole.Confirm(prompt, defaultValue: false))
        {
            return;
        }

        Console.Write("프록시 URL (예: http://proxy.corp:8080): ");
        var url = (Console.ReadLine() ?? string.Empty).Trim();
        if (url.Length == 0)
        {
            AnsiConsole.MarkupLine("[grey70]프록시 미설정[/]");
            return;
        }

        Console.Write("프록시 사용자 (없으면 Enter): ");
        var user = (Console.ReadLine() ?? string.Empty).Trim();
        string? pass = user.Length > 0 ? PasswordPrompt.Read("프록시 비밀번호: ") : null;

        var store = new FileCredentialStore();
        if (!string.IsNullOrEmpty(pass))
        {
            store.Set("PROXY_PASSWORD", pass);
        }

        SettingsWriter.Set(new Dictionary<string, string?>
        {
            ["proxyUrl"] = url,
            ["proxyUser"] = user.Length > 0 ? user : null,
        });

        var applied = ProxyConfig.Apply(url, user.Length > 0 ? user : null, store);
        AnsiConsole.MarkupLine(applied is not null
            ? $"[green]프록시 적용됨[/] [grey70]· {Markup.Escape(url)}[/]"
            : "[yellow]프록시 URL 형식이 올바르지 않습니다[/]");
    }

    public static void Logout()
    {
        // 키 제거 + 설정의 호스트/baseUrl 정리.
        var store = new FileCredentialStore();
        store.Set("OPENAI_API_KEY", string.Empty);
        SettingsWriter.Set(new Dictionary<string, string?>
        {
            ["baseUrl"] = null, ["host"] = null, ["account"] = null, ["loginAt"] = null,
        });
        AnsiConsole.MarkupLine("[grey70]로그아웃됨 (저장된 키 제거)[/]");
    }

    /// <summary>인증돼 있는지 (키 존재 여부). 첫 실행 자동 로그인 판단용.</summary>
    public static bool HasCredential()
    {
        var fromEnv = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(new FileCredentialStore().Get("OPENAI_API_KEY"));
    }
}
