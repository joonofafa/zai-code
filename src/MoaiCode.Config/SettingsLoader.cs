using System.Text.Json;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;

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

        var settings = Settings.Default;
        settings = ApplyLayer(
            settings,
            Path.Combine(home, ".claude", "settings.json"),
            allowSensitive: true,
            allowAutomation: true,
            allowPermissionRules: true);
        settings = ApplyLayer(
            settings,
            Path.Combine(home, ".moai", "settings.json"),
            allowSensitive: true,
            allowAutomation: true,
            allowPermissionRules: true);

        // 보안: 프로젝트 레이어는 민감/실행/권한 관련 키를 덮어쓰지 못한다.
        settings = ApplyLayer(
            settings,
            Path.Combine(workingDirectory, ".claude", "settings.json"),
            allowSensitive: false,
            allowAutomation: false,
            allowPermissionRules: false);

        return ApplyEnv(settings);
    }

    /// <summary>JSON 한 레이어를 머지 (없는 키는 기존 값 유지). 순수 함수 — 테스트 용이.</summary>
    public static Settings ApplyJson(Settings baseline, string json)
        => ApplyJson(baseline, json, allowSensitive: true, allowAutomation: true, allowPermissionRules: true);

    private static Settings ApplyJson(
        Settings baseline,
        string json,
        bool allowSensitive,
        bool allowAutomation,
        bool allowPermissionRules)
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
                ModelLow = GetString(root, "modelLow") ?? baseline.ModelLow,
                ModelMid = GetString(root, "modelMid") ?? baseline.ModelMid,
                ModelHigh = GetString(root, "modelHigh") ?? baseline.ModelHigh,
                Provider = allowSensitive ? GetString(root, "provider") ?? baseline.Provider : baseline.Provider,
                BaseUrl = allowSensitive ? GetString(root, "baseUrl", "base_url") ?? baseline.BaseUrl : baseline.BaseUrl,
                Language = L10n.NormalizeLanguage(GetString(root, "language", "locale", "uiLanguage", "ui_language"))
                           ?? baseline.Language,
                ReasoningEffort = NormalizeEffort(GetString(root, "reasoningEffort", "reasoning_effort", "effort"))
                                  ?? baseline.ReasoningEffort,
                Host = allowSensitive ? GetString(root, "host") ?? baseline.Host : baseline.Host,
                Account = allowSensitive ? GetString(root, "account", "email") ?? baseline.Account : baseline.Account,
                LoginAt = allowSensitive ? GetString(root, "loginAt", "login_at") ?? baseline.LoginAt : baseline.LoginAt,
                OrgName = allowSensitive ? GetString(root, "orgName", "org_name") ?? baseline.OrgName : baseline.OrgName,
                Name = allowSensitive ? GetString(root, "name", "userName") ?? baseline.Name : baseline.Name,
                AvailableModels = allowSensitive ? GetString(root, "availableModels") ?? baseline.AvailableModels : baseline.AvailableModels,
                ProxyUrl = allowSensitive ? GetString(root, "proxyUrl", "proxy_url", "proxy") ?? baseline.ProxyUrl : baseline.ProxyUrl,
                ProxyUser = allowSensitive ? GetString(root, "proxyUser", "proxy_user") ?? baseline.ProxyUser : baseline.ProxyUser,
                ProxyBypass = allowSensitive ? GetString(root, "proxyBypass", "proxy_bypass", "noProxy") ?? baseline.ProxyBypass : baseline.ProxyBypass,
                Permission = allowSensitive
                    ? ParsePermission(GetString(root, "permission", "permissionMode")) ?? baseline.Permission
                    : baseline.Permission,
                LogLevel = GetString(root, "logLevel", "log_level") ?? baseline.LogLevel,
                MaxTurns = GetInt(root, "maxTurns", "max_turns") ?? baseline.MaxTurns,
                OutputStyle = GetString(root, "outputStyle", "output_style") ?? baseline.OutputStyle,
                LintCommand = allowAutomation
                    ? GetString(root, "lintCommand", "lint_cmd", "lintCmd") ?? baseline.LintCommand
                    : baseline.LintCommand,
                TestCommand = allowAutomation
                    ? GetString(root, "testCommand", "test_cmd", "testCmd") ?? baseline.TestCommand
                    : baseline.TestCommand,
                AutoLint = allowAutomation
                    ? GetBool(root, "autoLint", "auto_lint") ?? baseline.AutoLint
                    : baseline.AutoLint,
                AutoTest = allowAutomation
                    ? GetBool(root, "autoTest", "auto_test") ?? baseline.AutoTest
                    : baseline.AutoTest,
                RepoMapTokens = GetInt(root, "repoMapTokens", "repo_map_tokens") ?? baseline.RepoMapTokens,
                Checkpoints = GetBool(root, "checkpoints", "autoCheckpoint", "auto_checkpoint")
                              ?? baseline.Checkpoints,
                ConfineToWorkspace = GetBool(root, "confineToWorkspace", "confine_to_workspace", "confine")
                              ?? baseline.ConfineToWorkspace,
                ContextWindowTokens = GetInt(root, "contextWindow", "context_window", "contextWindowTokens")
                                      ?? baseline.ContextWindowTokens,
                // 규칙은 사용자 레이어에서만 수용한다. 프로젝트 레이어는 권한 승격 방지.
                AllowRules = allowPermissionRules ? MergeRules(baseline.AllowRules, GetRuleList(root, "allow")) : baseline.AllowRules,
                DenyRules = allowPermissionRules ? MergeRules(baseline.DenyRules, GetRuleList(root, "deny")) : baseline.DenyRules,
            };
        }
    }

    private static Settings ApplyLayer(
        Settings baseline,
        string path,
        bool allowSensitive,
        bool allowAutomation,
        bool allowPermissionRules)
    {
        if (!File.Exists(path))
        {
            return baseline;
        }

        return ApplyJson(
            baseline,
            File.ReadAllText(path),
            allowSensitive,
            allowAutomation,
            allowPermissionRules);
    }

    public static Settings ApplyEnv(Settings baseline)
    {
        var model = Environment.GetEnvironmentVariable("MOAI_MODEL")
                    ?? Environment.GetEnvironmentVariable("OPENAI_MODEL");
        var baseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
        var language = L10n.NormalizeLanguage(Environment.GetEnvironmentVariable("MOAI_LANGUAGE"));
        var effort = NormalizeEffort(Environment.GetEnvironmentVariable("MOAI_REASONING_EFFORT")
                                     ?? Environment.GetEnvironmentVariable("OPENAI_REASONING_EFFORT")
                                     ?? Environment.GetEnvironmentVariable("MOAI_EFFORT"));
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
        var maxTurns = ParseInt(Environment.GetEnvironmentVariable("MOAI_MAX_TURNS"));
        var modelLow = Environment.GetEnvironmentVariable("MOAI_MODEL_LOW");
        var modelMid = Environment.GetEnvironmentVariable("MOAI_MODEL_MID");
        var modelHigh = Environment.GetEnvironmentVariable("MOAI_MODEL_HIGH");

        return baseline with
        {
            Model = model ?? baseline.Model,
            ModelLow = modelLow ?? baseline.ModelLow,
            ModelMid = modelMid ?? baseline.ModelMid,
            ModelHigh = modelHigh ?? baseline.ModelHigh,
            BaseUrl = baseUrl ?? baseline.BaseUrl,
            Language = language ?? baseline.Language,
            ReasoningEffort = effort ?? baseline.ReasoningEffort,
            ProxyUrl = proxy ?? baseline.ProxyUrl,
            Permission = perm ?? baseline.Permission,
            LintCommand = lint ?? baseline.LintCommand,
            TestCommand = test ?? baseline.TestCommand,
            AutoLint = autoLint ?? baseline.AutoLint,
            AutoTest = autoTest ?? baseline.AutoTest,
            Checkpoints = checkpoints ?? baseline.Checkpoints,
            ConfineToWorkspace = confine ?? baseline.ConfineToWorkspace,
            MaxTurns = maxTurns ?? baseline.MaxTurns,
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

    // permissions.{allow|deny} 배열(Claude Code 형식) 우선, 없으면 top-level {allow|deny}.
    private static IReadOnlyList<string> GetRuleList(JsonElement root, string key)
    {
        if (root.TryGetProperty("permissions", out var perms) &&
            perms.ValueKind == JsonValueKind.Object &&
            perms.TryGetProperty(key, out var arr) &&
            arr.ValueKind == JsonValueKind.Array)
        {
            return ReadStringArray(arr);
        }

        if (root.TryGetProperty(key, out var top) && top.ValueKind == JsonValueKind.Array)
        {
            return ReadStringArray(top);
        }

        return Array.Empty<string>();
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement arr)
    {
        var list = new List<string>();
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s)
            {
                list.Add(s);
            }
        }

        return list;
    }

    private static IReadOnlyList<string> MergeRules(IReadOnlyList<string> baseline, IReadOnlyList<string> added)
    {
        if (added.Count == 0)
        {
            return baseline;
        }

        var seen = new HashSet<string>(baseline, StringComparer.Ordinal);
        var merged = new List<string>(baseline);
        foreach (var r in added)
        {
            if (seen.Add(r))
            {
                merged.Add(r);
            }
        }

        return merged;
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

    private static int? ParseInt(string? value) =>
        int.TryParse(value, out var n) && n > 0 ? n : null;

    private static string? NormalizeEffort(string? value)
    {
        var v = value?.Trim().ToLowerInvariant();
        return v is "low" or "medium" or "high" ? v : null;
    }
}
