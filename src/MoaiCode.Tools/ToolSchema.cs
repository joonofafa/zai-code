using System.Text.Json;

namespace MoaiCode.Tools;

internal static class ToolSchema
{
    /// <summary>JSON Schema 문자열을 독립적인 JsonElement로 (JsonDocument 해제 후에도 유효).</summary>
    public static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public static string ResolvePath(string workingDirectory, string path)
    {
        // 셸이 아닌 툴 입력에는 ~ 가 확장되지 않으므로(예: Glob/Read), 선행 ~ 를 홈으로 펴준다.
        var expanded = ExpandHome(path);
        return Path.IsPathRooted(expanded)
            ? Path.GetFullPath(expanded)
            : Path.GetFullPath(Path.Combine(workingDirectory, expanded));
    }

    private static string ExpandHome(string path)
    {
        if (path == "~")
        {
            return Home;
        }

        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(Home, path[2..]);
        }

        return path;
    }

    private static string Home
        => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
