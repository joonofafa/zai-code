using System.CommandLine;
using System.Text.Json.Nodes;
using MoaiCode.Config;
using MoaiCode.Core;
using MoaiCode.Localization;
using MoaiCode.Providers;
using MoaiCode.Tui;

namespace MoaiCode.Cli;

/// <summary>
/// `mate providers` — /provider·/model 이 하는 일을 대화형 화면 없이 JSON 으로 내놓는다(웹 UI·브리지용).
/// 인자 없이: 현재 상태. `models &lt;id&gt;`: 그 프로바이더의 모델 목록. `set &lt;id&gt; --model …`: 선택을 저장하고 활성화.
/// `delete-key &lt;id&gt;`: 저장된 키 삭제.
/// API 키는 인자로 받지 않는다(프로세스 목록에 남는다) — `--key-stdin` 이면 표준 입력의 첫 줄을 키로 쓴다.
/// 런타임을 만들지 않는다. 저장값은 다음에 시작하는 mate 부터 적용된다(실행 중인 대화는 그대로).
/// </summary>
internal static class ProvidersCommand
{
    public static Command Build()
    {
        var cmd = new Command("providers", L10n.Get("cli.providers.description"));
        cmd.SetAction(_ => Print(State()));

        var idArg = new Argument<string>("id");
        var baseUrlOpt = new Option<string?>("--base-url") { Description = "API base URL (vLLM only)." };
        var keyStdinOpt = new Option<bool>("--key-stdin") { Description = "Read the API key from the first line of standard input." };

        var models = new Command("models", L10n.Get("cli.providers.models.description"));
        models.Arguments.Add(idArg);
        models.Options.Add(baseUrlOpt);
        models.Options.Add(keyStdinOpt);
        models.SetAction(async (ParseResult pr, CancellationToken ct) =>
        {
            if (ProviderCatalog.Parse(pr.GetValue(idArg)) is not { } kind)
            {
                return Fail("unknown provider");
            }

            var profile = Profile(kind);
            if (kind == ProviderKind.Vllm && ProviderCatalog.NormalizeBaseUrl(kind, pr.GetValue(baseUrlOpt)) is { } url)
            {
                profile = profile with { BaseUrl = url };
            }

            // 아직 저장하지 않은 키로 목록을 받아 본다 — 키가 맞는지 저장 전에 확인하는 용도.
            if (pr.GetValue(keyStdinOpt) && ReadKey() is { } typed)
            {
                profile = profile with { ApiKey = typed };
            }

            var result = await ModelDirectory.ListAsync(profile, ct).ConfigureAwait(false);
            return Print(new JsonObject
            {
                ["models"] = new JsonArray(result.Models.Select(m => (JsonNode)new JsonObject
                {
                    ["id"] = m,
                    ["contextWindow"] = result.ContextWindows is not null && result.ContextWindows.TryGetValue(m, out var w)
                        ? w
                        : kind == ProviderKind.Zai ? ZaiEndpoint.ContextWindow(m) : null,
                }).ToArray()),
                ["error"] = result.Error,
            });
        });
        cmd.Subcommands.Add(models);

        var modelOpt = new Option<string>("--model") { Required = true };
        var windowOpt = new Option<int?>("--context-window")
        {
            Description = "Context window in tokens. Omit to use the provider's default for the model.",
        };
        var visionOpt = new Option<string?>("--vision-model")
        {
            Description = "Image analysis model. Empty string turns image analysis off; omit to leave it unchanged.",
        };
        var set = new Command("set", L10n.Get("cli.providers.set.description"));
        set.Arguments.Add(idArg);
        set.Options.Add(modelOpt);
        set.Options.Add(windowOpt);
        set.Options.Add(baseUrlOpt);
        set.Options.Add(visionOpt);
        set.Options.Add(keyStdinOpt);
        set.SetAction(pr =>
        {
            if (ProviderCatalog.Parse(pr.GetValue(idArg)) is not { } kind)
            {
                return Fail("unknown provider");
            }

            var model = pr.GetValue(modelOpt)?.Trim();
            if (string.IsNullOrEmpty(model))
            {
                return Fail("model is required");
            }

            var window = pr.GetValue(windowOpt);
            if (window is { } w && (w < ModelPicker.MinContextWindow || w > ModelPicker.MaxContextWindow))
            {
                return Fail($"context window must be between {ModelPicker.MinContextWindow} and {ModelPicker.MaxContextWindow}");
            }

            var fields = new Dictionary<string, JsonNode?>
            {
                ["model"] = model,
                ["contextWindow"] = window is { } value ? JsonValue.Create(value) : null,   // null = 필드 삭제
            };
            if (pr.GetValue(visionOpt) is { } vision)
            {
                fields["visionModel"] = vision.Trim();   // 빈 문자열 = 사용 안 함(기본값과 구분)
            }

            if (kind == ProviderKind.Vllm)
            {
                var url = ProviderCatalog.NormalizeBaseUrl(kind, pr.GetValue(baseUrlOpt)) ?? Profile(kind).BaseUrl;
                if (url is null)
                {
                    return Fail("vllm needs --base-url");
                }

                fields["baseUrl"] = url;
            }

            var key = pr.GetValue(keyStdinOpt) ? ReadKey() : null;
            if (pr.GetValue(keyStdinOpt) && key is null)
            {
                return Fail("--key-stdin was given but no key arrived on standard input");
            }

            // 설정을 먼저 쓴다 — 깨진 settings.json 이라 거부되면 키도 건드리지 않는다(CliProviderSetup.Apply 와 같은 순서).
            SettingsWriter.SetProvider(ProviderCatalog.Id(kind), fields, activate: true);
            if (key is not null)
            {
                new FileCredentialStore().Set(ProviderCatalog.KeyName(kind), key);
            }

            return Print(State());
        });
        cmd.Subcommands.Add(set);

        // 저장된 키만 지운다(credentials.json). 환경변수로 들어오는 키는 런처·셸의 것이라 건드리지 않는다.
        var deleteKey = new Command("delete-key", L10n.Get("cli.providers.deleteKey.description"));
        deleteKey.Arguments.Add(idArg);
        deleteKey.SetAction(pr =>
        {
            if (ProviderCatalog.Parse(pr.GetValue(idArg)) is not { } kind)
            {
                return Fail("unknown provider");
            }

            new FileCredentialStore().Remove(ProviderCatalog.KeyName(kind));
            return Print(State());
        });
        cmd.Subcommands.Add(deleteKey);
        return cmd;
    }

    private static ProviderProfile Profile(ProviderKind kind) =>
        ProviderResolver.Resolve(
            SettingsLoader.Load(Directory.GetCurrentDirectory()) with { ActiveProvider = ProviderCatalog.Id(kind) },
            new FileCredentialStore());

    private static JsonObject State()
    {
        var settings = SettingsLoader.Load(Directory.GetCurrentDirectory());
        var stored = new FileCredentialStore();
        var active = ProviderCatalog.Parse(settings.ActiveProvider) ?? ProviderKind.Zai;
        return new JsonObject
        {
            ["active"] = ProviderCatalog.Id(active),
            // settings.json 최상위 contextWindow 는 프로바이더별 값보다 우선한다.
            ["fixedContextWindow"] = settings.ContextWindowTokens > 0 ? settings.ContextWindowTokens : null,
            ["providers"] = new JsonArray(ProviderCatalog.All.Select(kind =>
            {
                var p = Profile(kind);
                return (JsonNode)new JsonObject
                {
                    ["id"] = ProviderCatalog.Id(kind),
                    ["name"] = ProviderCatalog.DisplayName(kind),
                    ["baseUrl"] = p.BaseUrl,
                    ["baseUrlEditable"] = kind == ProviderKind.Vllm,
                    ["model"] = p.Model,
                    ["visionModel"] = p.VisionModel,
                    ["contextWindow"] = p.ContextWindow,
                    ["effectiveContextWindow"] = p.EffectiveContextWindow,
                    ["hasKey"] = !string.IsNullOrEmpty(p.ApiKey),
                    // stored = credentials.json(지울 수 있음), env = 환경변수(런처가 넣어 준 값).
                    ["keySource"] = !string.IsNullOrEmpty(stored.Get(ProviderCatalog.KeyName(kind))) ? "stored"
                        : !string.IsNullOrEmpty(p.ApiKey) ? "env" : null,
                    ["requiresKey"] = ProviderCatalog.RequiresKey(kind),
                    ["keyName"] = ProviderCatalog.KeyName(kind),
                    ["usable"] = p.IsUsable,
                };
            }).ToArray()),
        };
    }

    private static string? ReadKey()
    {
        var line = Console.In.ReadLine()?.Trim();
        return string.IsNullOrEmpty(line) ? null : line;
    }

    private static int Print(JsonObject json)
    {
        Console.WriteLine(json.ToJsonString());
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }
}
