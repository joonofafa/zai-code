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
    // 엔터프라이즈 배포 기본 호스트 (설정/인자로 오버라이드 가능).
    public const string DefaultHost = "https://vip.bccard.ai";

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
        });

        AnsiConsole.MarkupLine($"[green]✓ 로그인 완료[/] [grey70]· {Markup.Escape(model ?? "(모델 미선택)")} · {Markup.Escape(host)}[/]");
        return true;
    }

    public static void Logout()
    {
        // 키 제거 + 설정의 호스트/baseUrl 정리.
        var store = new FileCredentialStore();
        store.Set("OPENAI_API_KEY", string.Empty);
        SettingsWriter.Set(new Dictionary<string, string?> { ["baseUrl"] = null, ["host"] = null });
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
