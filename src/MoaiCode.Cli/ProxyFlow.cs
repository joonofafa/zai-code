using MoaiCode.Config;
using MoaiCode.Localization;
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
        AnsiConsole.MarkupLine($"[aqua]{Markup.Escape(L10n.Get("cli.proxy.title"))}[/] [grey70]· {Markup.Escape(L10n.Get("cli.proxy.subtitle"))}[/]");

        var cur = SettingsLoader.Load(Directory.GetCurrentDirectory()).ProxyUrl;
        if (!string.IsNullOrWhiteSpace(cur))
        {
            AnsiConsole.MarkupLine($"[grey70]{Markup.Escape(L10n.Get("cli.proxy.current", cur))}[/]");
        }

        Console.Write(L10n.Get("cli.proxy.serverPrompt"));
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
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(L10n.Get("common.unchanged"))}[/]");
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(L10n.Get("cli.proxy.invalidUrlEx"))}[/]");
            return false;
        }

        Console.Write(L10n.Get("cli.proxy.userPromptOpt"));
        var user = (Console.ReadLine() ?? string.Empty).Trim();
        string? password = null;
        if (user.Length > 0)
        {
            password = PasswordPrompt.Read(L10n.Get("cli.proxy.passwordPrompt"));
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

        var auth = user is null ? "" : L10n.Get("cli.proxy.userSuffix", Markup.Escape(user));
        AnsiConsole.MarkupLine($"[green]{Markup.Escape(L10n.Get("cli.proxy.configured"))}[/] [grey70]· {Markup.Escape(url)}{auth}[/]");
    }

    public static void Clear()
    {
        SettingsWriter.Set(new Dictionary<string, string?>
        {
            ["proxyUrl"] = null,
            ["proxyUser"] = null,
        });
        new FileCredentialStore().Set("PROXY_PASSWORD", string.Empty);
        AnsiConsole.MarkupLine($"[grey70]{Markup.Escape(L10n.Get("cli.proxy.cleared"))}[/]");
    }
}
