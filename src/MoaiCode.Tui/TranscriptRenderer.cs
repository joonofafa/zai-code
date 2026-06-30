using MoaiCode.Core.Messages;
using Spectre.Console;

namespace MoaiCode.Tui;

/// <summary>
/// 복원된 세션의 이전 대화를 화면에 다시 그린다 (사용자 입력 + assistant 응답 + 툴 호출 요약).
/// 시스템 프롬프트·system-reminder·tool_result 원문은 생략해 읽기 쉽게. 너무 길면 최근 N개만.
/// </summary>
public static class TranscriptRenderer
{
    private const int MaxMessages = 40;

    public static void Render(IReadOnlyList<Message> messages)
    {
        // 대화로 보일 메시지만 추림.
        var shown = messages.Where(IsConversational).ToList();
        var skipped = 0;
        if (shown.Count > MaxMessages)
        {
            skipped = shown.Count - MaxMessages;
            shown = shown.GetRange(shown.Count - MaxMessages, MaxMessages);
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(skipped > 0
            ? $"[grey70]──── 이전 대화 (최근 {MaxMessages}개, 앞 {skipped}개 생략) ────[/]"
            : "[grey70]──── 이전 대화 ────[/]");

        foreach (var m in shown)
        {
            switch (m)
            {
                case UserMessage u when u.Text.StartsWith("[Summary of earlier conversation]", StringComparison.Ordinal):
                    AnsiConsole.MarkupLine($"[grey70]{Markup.Escape(Clip(u.Text, 600))}[/]");
                    break;
                case UserMessage u:
                    AnsiConsole.MarkupLine($"[green]› [/]{Markup.Escape(u.Text)}");
                    break;
                case AssistantMessage a:
                    var text = string.Concat(a.Content.OfType<TextBlock>().Select(t => t.Text));
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        AnsiConsole.Write(new Panel(MarkdownRenderer.Render(text))
                            .Header("[aqua]MoAI Code[/]")
                            .Border(BoxBorder.Rounded)
                            .BorderColor(Color.Grey));
                    }

                    foreach (var tu in a.Content.OfType<ToolUseBlock>())
                    {
                        AnsiConsole.MarkupLine($"[grey70]→ {Markup.Escape(tu.Name)}[/]");
                    }

                    break;
            }
        }

        AnsiConsole.MarkupLine("[grey70]──── 여기서 이어집니다 ────[/]");
        AnsiConsole.WriteLine();
    }

    private static bool IsConversational(Message m) => m switch
    {
        UserMessage u => !u.Text.StartsWith("<system-reminder>", StringComparison.Ordinal),
        AssistantMessage => true,
        _ => false, // SystemMessage, ToolResultMessage 생략
    };

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
