using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoaiCode.Config;

namespace MoaiCode.Gui.ViewModels;

/// <summary>MoAI Desktop 로그인 + 사내 프록시 설정. 로그인 전에 프록시를 적용해 vip 에 접속한다.</summary>
public sealed partial class LoginViewModel : ObservableObject
{
    private const string DefaultHost = "https://vip.bccard.ai";
    private string? _mfaToken;

    [ObservableProperty] private string _proxyUrl = string.Empty;
    [ObservableProperty] private string _proxyUser = string.Empty;
    [ObservableProperty] private string _proxyPassword = string.Empty;
    [ObservableProperty] private bool _proxyExpanded;

    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _mfaCode = string.Empty;
    [ObservableProperty] private bool _mfaRequired;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private bool _busy;

    [ObservableProperty] private string _status = string.Empty;

    /// <summary>로그인 성공 시 발생.</summary>
    public event Action? LoggedIn;

    public LoginViewModel()
    {
        var s = SettingsLoader.Load(Directory.GetCurrentDirectory());
        ProxyUrl = s.ProxyUrl ?? string.Empty;
        ProxyUser = s.ProxyUser ?? string.Empty;
        Email = s.Account ?? string.Empty;
        ProxyExpanded = !string.IsNullOrWhiteSpace(s.ProxyUrl);
    }

    private bool CanLogin() => !Busy;

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private async Task Login()
    {
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password))
        {
            Status = "이메일과 비밀번호를 입력하세요.";
            return;
        }

        Busy = true;
        try
        {
            ApplyProxyIfAny();

            var host = ResolveHost();
            var client = new OpenMoaiClient(host);

            LoginResult r;
            if (MfaRequired && _mfaToken is not null)
            {
                Status = "MFA 확인 중…";
                r = await client.LoginMfaAsync(_mfaToken, MfaCode.Trim(), CancellationToken.None);
            }
            else
            {
                Status = "로그인 중…";
                r = await client.LoginAsync(Email.Trim(), Password, CancellationToken.None);
                if (r.Status == "mfa_required" && !string.IsNullOrEmpty(r.MfaToken))
                {
                    _mfaToken = r.MfaToken;
                    MfaRequired = true;
                    Status = "MFA 코드를 입력하고 다시 로그인하세요.";
                    return;
                }
            }

            if (r.Status != "ok" || string.IsNullOrEmpty(r.ApiKey))
            {
                Status = "로그인 실패: " + (r.Error ?? "알 수 없는 오류");
                MfaRequired = false;
                _mfaToken = null;
                return;
            }

            Save(host, r);
            Status = "로그인 완료";
            LoggedIn?.Invoke();
        }
        finally
        {
            Busy = false;
        }
    }

    private void ApplyProxyIfAny()
    {
        var url = ProxyUrl.Trim();
        if (url.Length == 0)
        {
            return;
        }

        var user = ProxyUser.Trim();
        var store = new FileCredentialStore();
        if (!string.IsNullOrEmpty(ProxyPassword))
        {
            store.Set("PROXY_PASSWORD", ProxyPassword);
        }

        SettingsWriter.Set(new Dictionary<string, string?>
        {
            ["proxyUrl"] = url,
            ["proxyUser"] = user.Length > 0 ? user : null,
        });

        ProxyConfig.Apply(url, user.Length > 0 ? user : null, store);
    }

    private void Save(string host, LoginResult r)
    {
        new FileCredentialStore().Set("OPENAI_API_KEY", r.ApiKey!);
        var baseUrl = string.IsNullOrEmpty(r.BaseUrl) ? host + "/api/v1" : r.BaseUrl;
        SettingsWriter.Set(new Dictionary<string, string?>
        {
            ["provider"] = "openai",
            ["host"] = host,
            ["baseUrl"] = baseUrl,
            ["model"] = r.DefaultModel,
            ["account"] = Email.Trim(),
            ["loginAt"] = DateTimeOffset.Now.ToString("o"),
            ["orgName"] = r.OrgName,
        });
    }

    private static string ResolveHost()
    {
        var env = Environment.GetEnvironmentVariable("MOAI_LOGIN_HOST");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env.Trim();
        }

        var s = SettingsLoader.Load(Directory.GetCurrentDirectory());
        return string.IsNullOrWhiteSpace(s.Host) ? DefaultHost : s.Host!.Trim();
    }
}
