using MoaiCode.Config;
using MoaiCode.Tui;
using Spectre.Console;

namespace MoaiCode.Cli;

/// <summary>
/// `moai proxy` — 사내망 프록시 설정. 프록시 서버(필수) + id/pw(선택) 입력.
/// URL/user 는 settings.json, 비번은 credentials(PROXY_PASSWORD, 0600)에 저장.
/// </summary>
public static class ProxyFlow
{
    /// <summary>대화형 설정. 빈 입력이면 취소.</summary>
    public static bool RunInteractive()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[aqua]MoAI Code 프록시 설정[/] [grey70]· 사내망 HTTP(S) 프록시[/]");

        var cur = SettingsLoader.Load(Directory.GetCurrentDirectory()).ProxyUrl;
        if (!string.IsNullOrWhiteSpace(cur))
        {
            AnsiConsole.MarkupLine($"[grey70]현재: {Markup.Escape(cur)}  (비우면 유지, 'off' 입력 시 해제)[/]");
        }

        Console.Write("프록시 서버 (예: http://proxy.corp:8080): ");
        var url = (Console.ReadLine() ?? string.Empty).Trim();

        if (url.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            url.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            Clear();
            return true;
        }

        if (url.Length == 0)
        {
            // 입력 없음: 기존 값 유지(취소).
            AnsiConsole.MarkupLine("[yellow]변경 없음[/]");
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            AnsiConsole.MarkupLine("[red]잘못된 URL 형식입니다 (예: http://proxy.corp:8080)[/]");
            return false;
        }

        Console.Write("프록시 사용자 (선택, 없으면 Enter): ");
        var user = (Console.ReadLine() ?? string.Empty).Trim();
        string? password = null;
        if (user.Length > 0)
        {
            password = PasswordPrompt.Read("프록시 비밀번호: ");
        }

        Set(url, user.Length > 0 ? user : null, password);
        return true;
    }

    /// <summary>비대화형 설정 (인자 기반). password 가 null 이고 user 가 있으면 기존 비번 유지.</summary>
    public static void Set(string url, string? user, string? password)
    {
        SettingsWriter.Set(new Dictionary<string, string?>
        {
            ["proxyUrl"] = url,
            ["proxyUser"] = user, // null 이면 키 제거
        });

        if (user is null)
        {
            new FileCredentialStore().Set("PROXY_PASSWORD", string.Empty);
        }
        else if (password is not null)
        {
            new FileCredentialStore().Set("PROXY_PASSWORD", password);
        }

        var auth = user is null ? "" : $" · 사용자 {Markup.Escape(user)}";
        AnsiConsole.MarkupLine($"[green]✓ 프록시 설정됨[/] [grey70]· {Markup.Escape(url)}{auth}[/]");
    }

    public static void Clear()
    {
        SettingsWriter.Set(new Dictionary<string, string?>
        {
            ["proxyUrl"] = null,
            ["proxyUser"] = null,
        });
        new FileCredentialStore().Set("PROXY_PASSWORD", string.Empty);
        AnsiConsole.MarkupLine("[grey70]프록시 해제됨[/]");
    }
}
