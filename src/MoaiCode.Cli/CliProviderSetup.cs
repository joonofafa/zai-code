using System.Text.Json.Nodes;
using MoaiCode.Config;
using MoaiCode.Core;
using MoaiCode.Providers;
using MoaiCode.Tui.Commands;

namespace MoaiCode.Cli;

/// <summary>
/// /provider·/model 의 저장·교체 구현. 키는 credentials.json(0600)에 프로바이더별로, 선택값은 settings.json 의
/// providers.&lt;id&gt; 에 두고, 대화 모델은 <see cref="SwitchableChatModel"/> 의 안쪽만 바꿔 다음 요청부터 반영한다.
/// 모델 목록이 알려 준 컨텍스트 창은 고른 모델과 함께 저장하고 엔진에 바로 적용한다.
/// </summary>
/// <param name="applyContextWindow">엔진 컨텍스트 창 변경. 설정에 contextWindow 를 고정했으면 null(바꾸지 않음).</param>
internal sealed class CliProviderSetup(
    string workingDirectory, SwitchableChatModel model, ICredentialStore credentials, Action<int>? applyContextWindow = null)
    : IProviderSetup
{
    // 마지막 모델 목록 조회가 알려 준 모델별 컨텍스트 창 — Apply·SetModel 이 고른 모델의 값을 찾는다.
    private IReadOnlyDictionary<string, int> _windows = new Dictionary<string, int>();

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
        _windows = result.ContextWindows ?? new Dictionary<string, int>();
        return (result.Models, result.Error);
    }

    public string Apply(ProviderProfile profile, string? newKey)
    {
        profile = profile with { ContextWindow = WindowOf(profile.Model) };

        // 설정을 먼저 쓴다 — 깨진 settings.json 이라 거부되면 아무것도 바꾸지 않은 채 예외가 올라간다.
        var fields = new Dictionary<string, JsonNode?>
        {
            ["model"] = profile.Model,
            ["visionModel"] = profile.VisionModel ?? string.Empty,   // 빈 문자열 = 사용 안 함(기본값과 구분)
        };
        SetWindowField(fields, profile.ContextWindow);
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
        applyContextWindow?.Invoke(profile.EffectiveContextWindow);
        return description;
    }

    public void SetModel(string chosen)
    {
        var current = Current with { Model = chosen, ContextWindow = WindowOf(chosen) };
        var fields = new Dictionary<string, JsonNode?> { ["model"] = chosen };
        SetWindowField(fields, current.ContextWindow);
        SettingsWriter.SetProvider(ProviderCatalog.Id(current.Kind), fields);
        ActiveProvider.Current = current;
        model.CurrentModel = chosen;
        applyContextWindow?.Invoke(current.EffectiveContextWindow);
    }

    private int? WindowOf(string? modelId)
        => modelId is not null && _windows.TryGetValue(modelId, out var w) ? w : null;

    // 목록이 창 크기를 알려 주지 않은 모델이면 예전 모델의 값이 남지 않게 지운다(null = 필드 삭제).
    private static void SetWindowField(Dictionary<string, JsonNode?> fields, int? window)
        => fields["contextWindow"] = window is { } w ? JsonValue.Create(w) : null;

    public void SetVisionModel(string visionModel)
    {
        var current = Current;
        SettingsWriter.SetProvider(
            ProviderCatalog.Id(current.Kind), new Dictionary<string, JsonNode?> { ["visionModel"] = visionModel });
        ActiveProvider.Current = current with { VisionModel = visionModel.Length == 0 ? null : visionModel };
    }
}
