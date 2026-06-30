using Spectre.Console;

namespace MoaiCode.Tui;

/// <summary>
/// Edit/Write 변경을 +/- 색상 diff로 콘솔에 출력. 공통 접두/접미 줄을 잘라
/// 실제 바뀐 구간만 보여주고, 너무 길면 잘라낸다(컨텍스트가 아니라 화면 표시용).
/// </summary>
internal static class DiffRenderer
{
    private const int MaxLines = 40;
    private const int MaxLineLen = 200;

    public static void Render(string oldText, string newText)
    {
        var oldLines = string.IsNullOrEmpty(oldText) ? Array.Empty<string>() : Split(oldText);
        var newLines = string.IsNullOrEmpty(newText) ? Array.Empty<string>() : Split(newText);

        // 공통 접두/접미 줄 제거 → 바뀐 가운데만 남긴다.
        var p = 0;
        while (p < oldLines.Length && p < newLines.Length && oldLines[p] == newLines[p])
        {
            p++;
        }

        var s = 0;
        while (s < oldLines.Length - p && s < newLines.Length - p
               && oldLines[oldLines.Length - 1 - s] == newLines[newLines.Length - 1 - s])
        {
            s++;
        }

        var removed = oldLines[p..(oldLines.Length - s)];
        var added = newLines[p..(newLines.Length - s)];

        if (removed.Length == 0 && added.Length == 0)
        {
            AnsiConsole.MarkupLine("[grey70]  (변경 없음)[/]");
            return;
        }

        var shown = 0;
        foreach (var line in removed)
        {
            if (shown++ >= MaxLines)
            {
                break;
            }

            AnsiConsole.MarkupLine($"[red]  - {Markup.Escape(Clip(line))}[/]");
        }

        foreach (var line in added)
        {
            if (shown++ >= MaxLines)
            {
                break;
            }

            AnsiConsole.MarkupLine($"[green]  + {Markup.Escape(Clip(line))}[/]");
        }

        var total = removed.Length + added.Length;
        if (total > MaxLines)
        {
            AnsiConsole.MarkupLine($"[grey70]  … (+{total - MaxLines} more diff lines)[/]");
        }
    }

    private static string[] Split(string s) => s.Replace("\r\n", "\n").Split('\n');

    private static string Clip(string s) => s.Length <= MaxLineLen ? s : s[..MaxLineLen] + "…";
}
