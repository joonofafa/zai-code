using System.Text.Json;
using System.Text.Json.Nodes;
using MoaiCode.Core;

namespace MoaiCode.Config;

/// <summary>~/.moai/settings.json 에 키 일부만 머지 저장 (기존 값 보존). 로그인/모델 전환에서 사용.</summary>
public static class SettingsWriter
{
    public static string DefaultPath => AppPaths.Combine("settings.json");

    public static void Set(IReadOnlyDictionary<string, string?> values, string? path = null)
    {
        path ??= DefaultPath;

        var obj = Load(path);

        foreach (var kv in values)
        {
            if (kv.Value is null)
            {
                obj.Remove(kv.Key);
            }
            else
            {
                obj[kv.Key] = kv.Value;
            }
        }

        Write(path, obj);
    }

    /// <summary>
    /// providers.&lt;id&gt; 의 필드 일부만 머지 저장(나머지 필드·다른 프로바이더 보존, 값 null 은 필드 삭제).
    /// 값은 JSON 노드라 숫자(contextWindow)는 숫자로 남는다 — 문자열로 쓰면 로더가 정수로 읽지 못해 조용히 무시된다.
    /// <paramref name="activate"/> 면 activeProvider 도 그 id 로 바꾼다.
    /// </summary>
    public static void SetProvider(
        string id, IReadOnlyDictionary<string, JsonNode?> fields, bool activate = false, string? path = null)
    {
        path ??= DefaultPath;

        var obj = Load(path);

        var providers = obj["providers"] as JsonObject ?? new JsonObject();
        var entry = providers[id] as JsonObject ?? new JsonObject();
        foreach (var kv in fields)
        {
            if (kv.Value is null)
            {
                entry.Remove(kv.Key);
            }
            else
            {
                entry[kv.Key] = kv.Value.DeepClone();
            }
        }

        providers[id] = entry;
        obj["providers"] = providers;
        if (activate)
        {
            obj["activeProvider"] = id;
        }

        Write(path, obj);
    }

    /// <summary>permissions.allow/deny 배열을 저장(기존 다른 키 보존).</summary>
    public static void SetPermissions(
        IReadOnlyList<string> allow, IReadOnlyList<string> deny, string? path = null)
    {
        path ??= DefaultPath;

        var obj = Load(path);

        var perms = obj["permissions"] as JsonObject ?? new JsonObject();
        perms["allow"] = ToArray(allow);
        perms["deny"] = ToArray(deny);
        obj["permissions"] = perms;

        Write(path, obj);
    }

    // 로더(SettingsLoader)와 같은 JSONC 규칙으로 읽는다 — 예전엔 주석·끝 쉼표가 있으면 파싱 실패를 빈 객체로
    // 삼켜 파일 전체를 새 키 하나로 덮어썼다(deny 규칙·프록시 소실). 정말 깨진 파일은 덮어쓰지 않고 거부한다.
    private static JsonObject Load(string path)
    {
        if (!File.Exists(path))
        {
            return new JsonObject();
        }

        try
        {
            var node = JsonNode.Parse(
                File.ReadAllText(path),
                documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
            return node as JsonObject ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{path} is not valid JSON; not overwriting it: {ex.Message}", ex);
        }
    }

    private static JsonArray ToArray(IReadOnlyList<string> items)
    {
        var arr = new JsonArray();
        foreach (var s in items)
        {
            arr.Add(s);
        }

        return arr;
    }

    private static void Write(string path, JsonObject obj)
    {
        // 설정에는 프록시 자격증명 등 민감값이 섞일 수 있다 — 보안 쓰기(임시파일 0600 + 원자적 rename).
        // 직접 truncate 쓰기는 크래시 시 파일을 망가뜨리므로 쓰지 않는다.
        var json = obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        SecureFile.Write(path, System.Text.Encoding.UTF8.GetBytes(json));
    }
}
