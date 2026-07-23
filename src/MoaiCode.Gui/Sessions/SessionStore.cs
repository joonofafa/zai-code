using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MoaiCode.Gui.Sessions;

/// <summary>대화 한 줄(role: user | assistant).</summary>
public sealed record TurnLine(string Role, string Text);

/// <summary>세션 목록 표시용 메타.</summary>
public sealed record SessionMeta(string Id, string Title, string When);

/// <summary>대화 기록을 ~/.moai/desktop-sessions/&lt;id&gt;.json 로 저장·불러온다.</summary>
public static class SessionStore
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".moai", "desktop-sessions");

    public static void Save(string id, IReadOnlyList<TurnLine> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Dir);
            var title = lines.FirstOrDefault(l => l.Role == "user")?.Text?.Trim() ?? "새 대화";
            title = title.Length > 40 ? title[..40] : title;
            var obj = new { id, title, savedAt = DateTimeOffset.Now.ToString("o"), lines };
            File.WriteAllText(Path.Combine(Dir, id + ".json"),
                JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = false }));
        }
        catch
        {
            // 저장 실패는 치명적 아님.
        }
    }

    public static IReadOnlyList<SessionMeta> List()
    {
        var result = new List<SessionMeta>();
        try
        {
            if (!Directory.Exists(Dir))
            {
                return result;
            }

            foreach (var f in Directory.GetFiles(Dir, "*.json").OrderByDescending(File.GetLastWriteTime))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(f));
                    var r = doc.RootElement;
                    var id = r.GetProperty("id").GetString()!;
                    var title = r.TryGetProperty("title", out var t) ? t.GetString() ?? "대화" : "대화";
                    result.Add(new SessionMeta(id, title, FormatWhen(File.GetLastWriteTime(f))));
                }
                catch
                {
                    // 손상 파일 건너뜀.
                }
            }
        }
        catch
        {
            // 무시.
        }

        return result;
    }

    public static IReadOnlyList<TurnLine> Load(string id)
    {
        try
        {
            var path = Path.Combine(Dir, id + ".json");
            if (!File.Exists(path))
            {
                return Array.Empty<TurnLine>();
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var lines = new List<TurnLine>();
            foreach (var l in doc.RootElement.GetProperty("lines").EnumerateArray())
            {
                lines.Add(new TurnLine(
                    l.GetProperty("Role").GetString() ?? "assistant",
                    l.GetProperty("Text").GetString() ?? string.Empty));
            }

            return lines;
        }
        catch
        {
            return Array.Empty<TurnLine>();
        }
    }

    private static string FormatWhen(DateTime dt)
    {
        var now = DateTime.Now;
        if (dt.Date == now.Date)
        {
            return "오늘";
        }

        if (dt.Date == now.Date.AddDays(-1))
        {
            return "어제";
        }

        return dt.ToString("M월 d일");
    }
}
