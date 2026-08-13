using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MoaiCode.Localization;

namespace MoaiCode.Gui.Sessions;

/// <summary>대화 한 줄(role: user | assistant).</summary>
public sealed record TurnLine(string Role, string Text);

/// <summary>세션 목록 표시용 메타. Kind: chat(일반) | generate(문서 생성) | edit(열린 문서 편집).
/// DocId: 연결된 문서의 MoAI 고유 식별자(MoaiDocId) — 같은 문서 = 같은 대화 재연결용.
/// DocPath: 연결된 문서의 마지막 전체 경로 — 파일 존재 확인/재연결 후보 탐색용.</summary>
public sealed record SessionMeta(
    string Id, string Title, string When, string Kind = "chat",
    string? TargetDoc = null, string? DocId = null, string? DocPath = null)
{
    /// <summary>연결된 문서 경로가 있는데 그 파일이 사라졌으면 true(이동/삭제 → orphaned).</summary>
    public bool IsMissing =>
        !string.IsNullOrEmpty(DocPath) && !System.IO.File.Exists(DocPath);

    /// <summary>히스토리 행 배지 아이콘(lucide 리소스 키).</summary>
    public string KindIcon => Kind switch
    {
        "edit" => "Icon.Pencil",
        "generate" => "Icon.Plus",
        _ => "Icon.MessageSquare",
    };

    /// <summary>편집 세션이면 대상 파일명, 아니면 빈 문자열(행에 부제로 표시).</summary>
    public string Subtitle => Kind == "edit" && !string.IsNullOrWhiteSpace(TargetDoc) ? TargetDoc! : When;
}

/// <summary>대화 기록을 ~/.moai/desktop-sessions/&lt;id&gt;.json 로 저장·불러온다.</summary>
public static class SessionStore
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".moai", "desktop-sessions");

    public static void Save(string id, IReadOnlyList<TurnLine> lines, string kind = "chat",
        string? targetDoc = null, string? docId = null, string? docPath = null)
    {
        if (lines.Count == 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Dir);
            var title = lines.FirstOrDefault(l => l.Role == "user")?.Text?.Trim() ?? L10n.Get("gui.session.newChat");
            title = title.Length > 40 ? title[..40] : title;
            var obj = new { id, title, kind, targetDoc, docId, docPath, savedAt = DateTimeOffset.Now.ToString("o"), lines };
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
                    var title = r.TryGetProperty("title", out var t) ? t.GetString() ?? L10n.Get("gui.session.chat") : L10n.Get("gui.session.chat");
                    var kind = r.TryGetProperty("kind", out var k) ? k.GetString() ?? "chat" : "chat";
                    var target = r.TryGetProperty("targetDoc", out var td) ? td.GetString() : null;
                    var docId = r.TryGetProperty("docId", out var di) ? di.GetString() : null;
                    var docPath = r.TryGetProperty("docPath", out var dp) ? dp.GetString() : null;
                    result.Add(new SessionMeta(id, title, FormatWhen(File.GetLastWriteTime(f)), kind, target, docId, docPath));
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

    /// <summary>대화 기록 파일을 삭제한다. 없거나 실패해도 조용히 넘어간다.</summary>
    public static void Delete(string id)
    {
        try
        {
            var path = Path.Combine(Dir, id + ".json");
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 삭제 실패는 치명적 아님.
        }
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
            return L10n.Get("gui.session.today");
        }

        if (dt.Date == now.Date.AddDays(-1))
        {
            return L10n.Get("gui.session.yesterday");
        }

        return dt.ToString(L10n.Get("gui.session.dateFormat"));
    }
}
