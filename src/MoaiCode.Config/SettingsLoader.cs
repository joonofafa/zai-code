using System.Text.Json;
using MoaiCode.Core.Tools;

namespace MoaiCode.Config;

/// <summary>
/// 3-tier 설정 로더. user(~/.claude, ~/.moai) → project(./.claude) → env 순으로
/// 머지하며 뒤 레이어가 앞을 덮어씀. JSONC(주석/trailing comma) 허용.
/// </summary>
public static class SettingsLoader
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Settings Load(string workingDirectory)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var layers = new[]
        {
            Path.Combine(home, ".claude", "settings.json"),
            Path.Combine(home, ".moai", "settings.json"),
            Path.Combine(workingDirectory, ".claude", "settings.json"),
        };

        var settings = Settings.Default;
        foreach (var path in layers)
        {
            if (File.Exists(path))
            {
                settings = ApplyJson(settings, File.ReadAllText(path));
            }
        }

        return ApplyEnv(settings);
    }

    /// <summary>JSON 한 레이어를 머지 (없는 키는 기존 값 유지). 순수 함수 — 테스트 용이.</summary>
    public static Settings ApplyJson(Settings baseline, string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, JsonOptions);
        }
        catch (JsonException)
        {
            return baseline;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return baseline;
            }

            return baseline with
            {
                Model = GetString(root, "model", "model_id") ?? baseline.Model,
                Provider = GetString(root, "provider") ?? baseline.Provider,
                BaseUrl = GetString(root, "baseUrl", "base_url") ?? baseline.BaseUrl,
                ProxyUrl = GetString(root, "proxyUrl", "proxy_url", "proxy") ?? baseline.ProxyUrl,
                ProxyUser = GetString(root, "proxyUser", "proxy_user") ?? baseline.ProxyUser,
                Permission = ParsePermission(GetString(root, "permission", "permissionMode"))
                             ?? baseline.Permission,
                MaxTurns = GetInt(root, "maxTurns", "max_turns") ?? baseline.MaxTurns,
                OutputStyle = GetString(root, "outputStyle", "output_style") ?? baseline.OutputStyle,
                LintCommand = GetString(root, "lintCommand", "lint_cmd", "lintCmd") ?? baseline.LintCommand,
                TestCommand = GetString(root, "testCommand", "test_cmd", "testCmd") ?? baseline.TestCommand,
                AutoLint = GetBool(root, "autoLint", "auto_lint") ?? baseline.AutoLint,
                AutoTest = GetBool(root, "autoTest", "auto_test") ?? baseline.AutoTest,
                RepoMapTokens = GetInt(root, "repoMapTokens", "repo_map_tokens") ?? baseline.RepoMapTokens,
                Checkpoints = GetBool(root, "checkpoints", "autoCheckpoint", "auto_checkpoint")
                              ?? baseline.Checkpoints,
                ConfineToWorkspace = GetBool(root, "confineToWorkspace", "confine_to_workspace", "confine")
                              ?? baseline.ConfineToWorkspace,
                ContextWindowTokens = GetInt(root, "contextWindow", "context_window", "contextWindowTokens")
                                      ?? baseline.ContextWindowTokens,
            };
        }
    }

    public static Settings ApplyEnv(Settings baseline)
    {
        var model = Environment.GetEnvironmentVariable("MOAI_MODEL")
                    ?? Environment.GetEnvironmentVariable("OPENAI_MODEL");
        var baseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
        var proxy = Environment.GetEnvironmentVariable("MOAI_PROXY")
                    ?? Environment.GetEnvironmentVariable("HTTPS_PROXY")
                    ?? Environment.GetEnvironmentVariable("https_proxy")
                    ?? Environment.GetEnvironmentVariable("HTTP_PROXY")
                    ?? Environment.GetEnvironmentVariable("http_proxy");
        var perm = ParsePermission(Environment.GetEnvironmentVariable("MOAI_PERMISSION"));
        var lint = Environment.GetEnvironmentVariable("MOAI_LINT_CMD");
        var test = Environment.GetEnvironmentVariable("MOAI_TEST_CMD");
        var autoLint = ParseBool(Environment.GetEnvironmentVariable("MOAI_AUTO_LINT"));
        var autoTest = ParseBool(Environment.GetEnvironmentVariable("MOAI_AUTO_TEST"));
        var checkpoints = ParseBool(Environment.GetEnvironmentVariable("MOAI_CHECKPOINTS"));
        var confine = ParseBool(Environment.GetEnvironmentVariable("MOAI_CONFINE_WORKSPACE"));

        return baseline with
        {
            Model = model ?? baseline.Model,
            BaseUrl = baseUrl ?? baseline.BaseUrl,
            ProxyUrl = proxy ?? baseline.ProxyUrl,
            Permission = perm ?? baseline.Permission,
            LintCommand = lint ?? baseline.LintCommand,
            TestCommand = test ?? baseline.TestCommand,
            AutoLint = autoLint ?? baseline.AutoLint,
            AutoTest = autoTest ?? baseline.AutoTest,
            Checkpoints = checkpoints ?? baseline.Checkpoints,
            ConfineToWorkspace = confine ?? baseline.ConfineToWorkspace,
        };
    }

    private static string? GetString(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrEmpty(s))
                {
                    return s;
                }
            }
        }

        return null;
    }

    private static int? GetInt(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
                && v.TryGetInt32(out var n))
            {
                return n;
            }
        }

        return null;
    }

    private static bool? GetBool(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!obj.TryGetProperty(key, out var v))
            {
                continue;
            }

            if (v.ValueKind is JsonValueKind.True)
            {
                return true;
            }

            if (v.ValueKind is JsonValueKind.False)
            {
                return false;
            }
        }

        return null;
    }

    private static PermissionMode? ParsePermission(string? value) => value?.ToLowerInvariant() switch
    {
        "ask" => PermissionMode.Ask,
        "auto" => PermissionMode.Auto,
        "deny" => PermissionMode.Deny,
        _ => null,
    };

    private static bool? ParseBool(string? value) => value?.ToLowerInvariant() switch
    {
        "1" or "true" or "yes" or "on" => true,
        "0" or "false" or "no" or "off" => false,
        _ => null,
    };
}
