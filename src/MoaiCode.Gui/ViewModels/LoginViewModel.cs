using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    private readonly CancellationTokenSource _cts = new();
    private string? _mfaToken;

    /// <summary>진행 중인 로그인을 취소(창이 로그인 없이 닫힐 때).</summary>
    public void Cancel() => _cts.Cancel();

    [ObservableProperty] private string _proxyUrl = string.Empty;
    [ObservableProperty] private string _proxyUser = string.Empty;
    [ObservableProperty] private string _proxyPassword = string.Empty;
    [ObservableProperty] private bool _proxyExpanded;

    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _mfaCode = string.Empty;
    [ObservableProperty] private bool _mfaRequired;

    // 로그인 후 모델 선택 단계.
    [ObservableProperty] private bool _modelPickStage;
    [ObservableProperty] private string? _selectedModel;
    public System.Collections.ObjectModel.ObservableCollection<string> Models { get; } = new();
    private LoginResult? _pending;
    private string? _pendingHost;

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
            var wasMfa = MfaRequired && _mfaToken is not null;

            // 프록시는 최초 로그인 시도에서만 적용(MFA 재클릭 시 재적용 불필요).
            if (!wasMfa)
            {
                ApplyProxyIfAny();
            }

            var host = ResolveHost();
            var client = new OpenMoaiClient(host);

            LoginResult r;
            if (wasMfa)
            {
                Status = "MFA 확인 중…";
                r = await client.LoginMfaAsync(_mfaToken!, MfaCode.Trim(), _cts.Token);
            }
            else
            {
                Status = "로그인 중…";
                r = await client.LoginAsync(Email.Trim(), Password, _cts.Token);
                if (r.Status == "mfa_required" && !string.IsNullOrEmpty(r.MfaToken))
                {
                    _mfaToken = r.MfaToken;
                    MfaRequired = true;
                    Status = "MFA 코드를 입력하고 다시 로그인하세요.";
                    return;
                }
            }

            if (_cts.IsCancellationRequested)
            {
                return; // 창이 닫혀 취소됨 — 저장/이벤트 없이 종료.
            }

            if (r.Status != "ok" || string.IsNullOrEmpty(r.ApiKey))
            {
                Status = "로그인 실패: " + (r.Error ?? "알 수 없는 오류");
                if (!wasMfa)
                {
                    // 최초 로그인 실패만 초기화. MFA 코드 오류면 코드만 다시 입력하도록 유지.
                    MfaRequired = false;
                    _mfaToken = null;
                }

                return;
            }

            // 로그인 성공 → 모델 선택 단계로. (모델 목록이 없으면 바로 진행)
            _pending = r;
            _pendingHost = host;
            Models.Clear();
            foreach (var m in r.Models)
            {
                Models.Add(m);
            }

            SelectedModel = !string.IsNullOrEmpty(r.DefaultModel) ? r.DefaultModel : r.Models.FirstOrDefault();

            if (Models.Count == 0)
            {
                Save(host, r, SelectedModel);
                LoggedIn?.Invoke();
                return;
            }

            ModelPickStage = true;
            Status = (string.IsNullOrEmpty(r.Name) ? "" : r.Name + " 님, ") + "사용할 모델을 선택하고 시작하세요.";
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>모델 선택 후 시작.</summary>
    [RelayCommand]
    private void Start()
    {
        if (_pending is null)
        {
            return;
        }

        Save(_pendingHost!, _pending, SelectedModel);
        LoggedIn?.Invoke();
    }

    private void ApplyProxyIfAny()
    {
        var url = ProxyUrl.Trim();
        if (url.Length > 0)
        {
            ProxyConfig.Save(url, ProxyUser, ProxyPassword);
        }
    }

    private void Save(string host, LoginResult r, string? chosenModel)
    {
        var baseUrl = string.IsNullOrEmpty(r.BaseUrl) ? host + "/api/v1" : r.BaseUrl;
        var model = !string.IsNullOrEmpty(chosenModel) ? chosenModel
            : !string.IsNullOrEmpty(r.DefaultModel) ? r.DefaultModel : r.Models.FirstOrDefault();

        new FileCredentialStore().Set("OPENAI_API_KEY", r.ApiKey!);

        // 재로그인이 즉시 반영되도록 환경변수를 강제 갱신한다. GuiBootstrap 은 env 가 이미
        // 설정돼 있으면 스킵하므로(첫 빌드 때 세팅됨), 강제로 덮어써야 새 키/URL/모델이 적용된다.
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", r.ApiKey);
        Environment.SetEnvironmentVariable("OPENAI_BASE_URL", baseUrl);
        if (!string.IsNullOrEmpty(model))
        {
            Environment.SetEnvironmentVariable("MOAI_MODEL", model);
        }

        var values = new Dictionary<string, string?>
        {
            ["provider"] = "openai",
            ["host"] = host,
            ["baseUrl"] = baseUrl,
            ["account"] = Email.Trim(),
            ["loginAt"] = DateTimeOffset.Now.ToString("o"),
            ["orgName"] = r.OrgName,
            ["name"] = r.Name,
            ["availableModels"] = r.Models.Count > 0 ? string.Join(",", r.Models) : null,
        };
        if (!string.IsNullOrEmpty(model))
        {
            values["model"] = model;
        }

        SettingsWriter.Set(values);
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
