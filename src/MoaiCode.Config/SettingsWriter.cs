using System.Text.Json;
using System.Text.Json.Nodes;

namespace MoaiCode.Config;

/// <summary>~/.moai/settings.json 에 키 일부만 머지 저장 (기존 값 보존). 로그인/모델 전환에서 사용.</summary>
public static class SettingsWriter
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".moai", "settings.json");

    public static void Set(IReadOnlyDictionary<string, string?> values, string? path = null)
    {
        path ??= DefaultPath;

        JsonObject obj;
        try
        {
            obj = (File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null) ?? new JsonObject();
        }
        catch
        {
            obj = new JsonObject();
        }

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

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
