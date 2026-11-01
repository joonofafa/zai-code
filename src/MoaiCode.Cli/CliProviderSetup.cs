using MoaiCode.Config;
using MoaiCode.Core;
using MoaiCode.Providers;
using MoaiCode.Tui.Commands;

namespace MoaiCode.Cli;

/// <summary>
/// /provider·/model 의 저장·교체 구현. 키는 credentials.json(0600)에 프로바이더별로, 선택값은 settings.json 의
/// providers.&lt;id&gt; 에 두고, 대화 모델은 <see cref="SwitchableChatModel"/> 의 안쪽만 바꿔 다음 요청부터 반영한다.
/// </summary>
internal sealed class CliProviderSetup(string workingDirectory, SwitchableChatModel model, ICredentialStore credentials)
    : IProviderSetup
{
    public ProviderProfile Current => ActiveProvider.Current;

    public ProviderProfile Effective(ProviderKind kind)
        => kind == Current.Kind
            ? Current
            : ProviderResolver.Resolve(
                SettingsLoader.Load(workingDirectory) with { ActiveProvider = ProviderCatalog.Id(kind) }, credentials);

    public bool HasStoredKey(ProviderKind kind)
        => !string.IsNullOrEmpty(credentials.Get(ProviderCatalog.KeyName(kind)));

    public async Task<(IReadOnlyList<string> Models, string? Error)> ListModelsAsync(
        ProviderProfile candidate, CancellationToken ct)
    {
        var result = await ModelDirectory.ListAsync(candidate, ct).ConfigureAwait(false);
        return (result.Models, result.Error);
    }

    public string Apply(ProviderProfile profile, string? newKey)
    {
        // 설정을 먼저 쓴다 — 깨진 settings.json 이라 거부되면 아무것도 바꾸지 않은 채 예외가 올라간다.
        var fields = new Dictionary<string, string?>
        {
            ["model"] = profile.Model,
            ["visionModel"] = profile.VisionModel ?? string.Empty,   // 빈 문자열 = 사용 안 함(기본값과 구분)
        };
        if (profile.Kind == ProviderKind.Vllm)
        {
            fields["baseUrl"] = profile.BaseUrl;
        }

        SettingsWriter.SetProvider(ProviderCatalog.Id(profile.Kind), fields, activate: true);
        if (newKey is not null)
        {
            credentials.Set(ProviderCatalog.KeyName(profile.Kind), newKey);
        }

        ActiveProvider.Current = profile;
        model.Inner = ProviderFactory.Create(profile, out var description);
        return description;
    }

    public void SetVisionModel(string visionModel)
    {
        var current = Current;
        SettingsWriter.SetProvider(
            ProviderCatalog.Id(current.Kind), new Dictionary<string, string?> { ["visionModel"] = visionModel });
        ActiveProvider.Current = current with { VisionModel = visionModel.Length == 0 ? null : visionModel };
    }
}
