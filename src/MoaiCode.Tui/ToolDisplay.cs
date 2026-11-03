using System.Text.Json;
using MoaiCode.Localization;

namespace MoaiCode.Tui;

/// <summary>
/// 툴 호출 입력(JSON)을 사람이 읽기 쉬운 형태로 변환 (권한 다이얼로그 등).
/// 특히 Bash 는 raw JSON 대신 실제 명령어를 터미널 형태로 보여준다.
/// </summary>
public static class ToolDisplay
{
    /// <summary>
    /// 권한 확인에 보일 문구. 길면 앞과 끝을 함께 남긴다 — 예전엔 앞 2000자만 보여서, 긴 공백 뒤에 숨긴
    /// 파괴적 꼬리(`… ; rm -rf /data`)를 사람이 못 보고 승인할 수 있었다(2026-11-03 검토).
    /// </summary>
    public static string ForPrompt(string tool, JsonElement input)
    {
        var display = Describe(tool, input);
        if (display.Length <= 2000)
        {
            return display;
        }

        return display[..1200]
            + "\n" + MoaiCode.Localization.L10n.Get("permission.omitted", display.Length - 1800) + "\n"
            + display[^600..];
    }

    public static string Describe(string tool, JsonElement input)
    {
        switch (tool)
        {
            case "Bash":
                var cmd = Str(input, "command");
                var bg = input.TryGetProperty("run_in_background", out var b) && b.ValueKind == JsonValueKind.True ? " &" : "";
                return string.IsNullOrWhiteSpace(cmd) ? L10n.Get("tool.emptyCommand") : "$ " + cmd.Trim() + bg;
            case "BashOutput":
                return "BashOutput " + (Str(input, "shell_id") ?? "");
            case "KillShell":
                return "KillShell " + (Str(input, "shell_id") ?? "");

            case "Read":
                return "Read " + (Str(input, "path") ?? "");
            case "Write":
                return "Write " + (Str(input, "path") ?? "");
            case "Edit":
                return "Edit " + (Str(input, "path") ?? "");

            case "Grep":
                return "Grep " + Quote(Str(input, "pattern")) + In(Str(input, "path"));
            case "Glob":
                return "Glob " + (Str(input, "pattern") ?? "") + In(Str(input, "path"));

            case "WebFetch":
                return "WebFetch " + (Str(input, "url") ?? "");

            case "Agent":
                return "Agent: " + (Str(input, "description") ?? Str(input, "prompt") ?? "");
            case "Skill":
                return "Skill " + (Str(input, "name") ?? "");

            case "TaskCreate":
                return "TaskCreate: " + (Str(input, "subject") ?? "");
            case "TaskUpdate":
                return "TaskUpdate #" + (Str(input, "id") ?? "?") + " → " + (Str(input, "status") ?? "?");
            case "TaskList":
                return "TaskList";
            case "AskUserQuestion":
                return "AskUserQuestion" + (Str(input, "question") is { } q ? ": " + q : string.Empty);

            default:
                // MCP/미지원 툴 → raw 한 줄 대신 보기 좋게 들여쓴 JSON.
                return PrettyJson(input);
        }
    }

    private static string In(string? path)
        => string.IsNullOrWhiteSpace(path) || path == "." ? string.Empty : $"  (in {path})";

    private static string Quote(string? s)
        => string.IsNullOrEmpty(s) ? "" : "\"" + s + "\"";

    private static string? Str(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string PrettyJson(JsonElement el)
    {
        try
        {
            return JsonSerializer.Serialize(el, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return el.GetRawText();
        }
    }
}
