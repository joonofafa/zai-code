using MoaiCode.Core;
using MoaiCode.Localization;

namespace MoaiCode.Tui.Commands;

/// <summary>
/// /provider·/model 이 쓰는 프로바이더 설정 창구. 저장(settings·credentials)과 라이브 모델 교체는 Cli 가 구현한다
/// (Tui 는 Config·Providers 를 참조하지 않는다).
/// </summary>
public interface IProviderSetup
{
    /// <summary>지금 대화에 쓰는 프로바이더.</summary>
    ProviderProfile Current { get; }

    /// <summary>그 프로바이더로 바꾼다면 쓰일 값(저장값 → 환경변수 → 기본값).</summary>
    ProviderProfile Effective(ProviderKind kind);

    /// <summary>키가 credentials.json 에 저장돼 있는지(아니면 환경변수에서 온 키이거나 없음).</summary>
    bool HasStoredKey(ProviderKind kind);

    /// <summary>접속 확인 겸 모델 목록 조회. 실패하면 Error 에 이유.</summary>
    Task<(IReadOnlyList<string> Models, string? Error)> ListModelsAsync(ProviderProfile candidate, CancellationToken ct);

    /// <summary>저장하고 대화 모델을 바꾼다. <paramref name="newKey"/> 가 null 이면 키는 그대로 둔다. 반환은 프로바이더 설명.</summary>
    string Apply(ProviderProfile profile, string? newKey);

    /// <summary>현재 프로바이더의 대화 모델을 바꾸고 저장한다(/model). 컨텍스트 창도 그 모델에 맞춘다.</summary>
    void SetModel(string model);

    /// <summary>현재 프로바이더의 이미지 분석 모델을 저장·적용한다. 빈 문자열은 "사용 안 함".</summary>
    void SetVisionModel(string visionModel);
}

/// <summary>/provider — 프로바이더 선택 → API 키(붙여넣기·마스킹) → (vLLM 주소) → 접속 확인 → 모델·이미지 분석 모델 선택 → 저장.</summary>
internal sealed class ProviderCommand : ISlashCommand
{
    public string Name => "provider";
    public string Description => L10n.Get("slash.provider.description");

    public async Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct)
    {
        if (ctx.ProviderSetup is null)
        {
            return new SlashResult(L10n.Get("slash.provider.unsupported"));
        }

        if (Console.IsInputRedirected)
        {
            return new SlashResult(L10n.Get("slash.provider.noSelectNonInteractive"));
        }

        return new SlashResult(await ProviderFlow.RunAsync(PromptIo.Terminal, ctx.ProviderSetup, ct).ConfigureAwait(false));
    }
}

/// <summary>/provider 대화 흐름 본체. 접속 확인에 성공하고 모델까지 골라야 저장한다 — 중간에 취소하거나 실패하면 아무것도 바꾸지 않는다.</summary>
internal static class ProviderFlow
{
    public static async Task<string> RunAsync(PromptIo io, IProviderSetup setup, CancellationToken ct)
    {
        var current = setup.Current;
        var kinds = ProviderCatalog.All;
        var items = kinds.Select(k => Label(setup, current, k)).ToList();
        var pick = io.Select(L10n.Get("slash.provider.pickTitle"), items, Math.Max(0, IndexOf(kinds, current.Kind)));
        if (pick < 0)
        {
            return L10n.Get("common.unchanged");
        }

        var kind = kinds[pick];
        var effective = setup.Effective(kind);

        // 1) API 키 — Enter 는 지금 키 유지.
        var keyLabel = effective.ApiKey is { } existing
            ? L10n.Get("slash.provider.keyPromptKeep", SecretMask.Mask(existing))
            : ProviderCatalog.RequiresKey(kind)
                ? L10n.Get("slash.provider.keyPrompt")
                : L10n.Get("slash.provider.keyPromptOptional");
        var typed = io.ReadSecret(keyLabel);
        if (typed is null)
        {
            return L10n.Get("common.unchanged");
        }

        var newKey = typed.Length == 0 ? null : typed;
        var key = newKey ?? effective.ApiKey;
        if (key is null && ProviderCatalog.RequiresKey(kind))
        {
            return L10n.Get("slash.provider.keyRequired", ProviderCatalog.DisplayName(kind));
        }

        // 2) 서버 주소 — vLLM 만 묻는다(나머지는 공식 주소).
        var baseUrl = effective.BaseUrl;
        if (kind == ProviderKind.Vllm)
        {
            var typedUrl = io.ReadLine(baseUrl is null
                ? L10n.Get("slash.provider.urlPrompt")
                : L10n.Get("slash.provider.urlPromptKeep", baseUrl));
            if (typedUrl is null)
            {
                return L10n.Get("common.unchanged");
            }

            if (typedUrl.Trim().Length > 0)
            {
                baseUrl = ProviderCatalog.NormalizeBaseUrl(kind, typedUrl);
                if (baseUrl is null)
                {
                    return L10n.Get("slash.provider.badUrl", typedUrl.Trim());
                }
            }

            if (baseUrl is null)
            {
                return L10n.Get("slash.provider.urlRequired");
            }
        }

        // 3) 접속 확인 = 모델 목록 조회. 실패하면 저장하지 않는다.
        var candidate = effective with { ApiKey = key, BaseUrl = baseUrl };
        io.Info(L10n.Get("slash.provider.connecting", ProviderCatalog.DisplayName(kind)));
        var (models, error) = await setup.ListModelsAsync(candidate, ct).ConfigureAwait(false);
        if (error is not null)
        {
            return L10n.Get("slash.provider.connectFailed", ProviderCatalog.DisplayName(kind), error);
        }

        // 4) 모델 · 이미지 분석 모델.
        var model = ModelPicker.Pick(io, models, effective.Model);
        if (model is null)
        {
            return L10n.Get("common.unchanged");
        }

        var vision = ModelPicker.PickVision(io, models, effective.VisionModel);
        if (vision is null)
        {
            return L10n.Get("common.unchanged");
        }

        try
        {
            var description = setup.Apply(
                candidate with { Model = model, VisionModel = vision.Length == 0 ? null : vision }, newKey);
            return L10n.Get("slash.provider.switched", description);
        }
        catch (InvalidOperationException ex)   // settings.json 이 깨져 덮어쓰기를 거부한 경우 등
        {
            return L10n.Get("slash.provider.saveFailed", ex.Message);
        }
    }

    // "OpenRouter  (current · key sk-or-v1****)" — 지금 쓰는 것과 키 보유 여부를 목록에서 바로 보이게.
    private static string Label(IProviderSetup setup, ProviderProfile current, ProviderKind kind)
    {
        var notes = new List<string>();
        if (kind == current.Kind)
        {
            notes.Add(L10n.Get("slash.provider.current"));
        }

        if (setup.HasStoredKey(kind))
        {
            notes.Add(L10n.Get("slash.provider.keySaved"));
        }

        var name = ProviderCatalog.DisplayName(kind);
        return notes.Count == 0 ? name : $"{name}  ({string.Join(" · ", notes)})";
    }

    private static int IndexOf(IReadOnlyList<ProviderKind> kinds, ProviderKind kind)
    {
        for (var i = 0; i < kinds.Count; i++)
        {
            if (kinds[i] == kind)
            {
                return i;
            }
        }

        return -1;
    }
}
