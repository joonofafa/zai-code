using System.Runtime.CompilerServices;
using System.Text.Json;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;
using Spectre.Console;

namespace MoaiCode.Tui;

/// <summary>
/// 사용자에게 선택지를 제시하고 고르게 하는 툴. 평문으로 "1/2/3 중 무엇?"이라고 묻는 대신
/// 이 툴을 호출하면 TUI가 번호 선택 UI를 띄우고, 고른 항목을 결과로 모델에 돌려준다.
/// 단일 파일 게시 호환을 위해 Spectre SelectionPrompt 가 아닌 일반 Console 입력을 쓴다.
/// </summary>
public sealed class AskUserQuestionTool : ITool
{
    public string Name => "AskUserQuestion";

    public string Description => """
        Ask the user to choose between options when a decision is genuinely the user's to make
        (e.g., which approach to take, which file to target). Presents a selectable numbered list
        and returns the user's choice. Prefer this over asking in plain prose when you want the
        user to pick from concrete options. Provide a clear question and 2-4 concise options.
        The UI always adds an "Other" entry so the user can type a free-form answer instead of
        picking one of your options; treat such a custom answer as their instruction.
        """;

    public bool IsReadOnly => true;          // 사용자에게 묻는 행위 — 권한 게이트 불필요
    public bool IsConcurrencySafe => false;  // 콘솔 입력을 점유

    public JsonElement InputSchema { get; } = ParseSchema(
        """
        {
          "type": "object",
          "properties": {
            "question": { "type": "string", "description": "The question to ask the user" },
            "options": {
              "type": "array",
              "description": "2-4 options to choose from",
              "items": {
                "type": "object",
                "properties": {
                  "label": { "type": "string" },
                  "description": { "type": "string" }
                },
                "required": ["label"]
              }
            }
          },
          "required": ["question", "options"]
        }
        """);

    private static JsonElement ParseSchema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;

        var question = GetStr(input, "question") ?? "";
        var options = ParseOptions(input);
        if (string.IsNullOrWhiteSpace(question) || options.Count == 0)
        {
            yield return new ToolOutput("AskUserQuestion: 'question' and non-empty 'options' are required", IsError: true);
            yield break;
        }

        // 비대화형(파이프/헤드리스)에서는 물어볼 수 없다 → 모델이 알아서 진행하도록 안내.
        if (Console.IsInputRedirected)
        {
            yield return new ToolOutput(
                "No interactive user is available to answer. Proceed using your best judgment and state the assumption.");
            yield break;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(new Markup(Markup.Escape(question)))
            .Header($"[aqua]{Markup.Escape(L10n.Get("common.question"))}[/]")
            .BorderColor(Color.Aqua));

        // 화살표 선택 위젯. label + (설명) 을 항목으로. 마지막에 '직접 입력…'(자유 텍스트)을 항상 추가해,
        // 제시된 선택지가 안 맞을 때 ESC로 빠져나가(→ 불필요한 재질문 턴) 대신 사용자가 답을 직접 줄 수 있게 한다.
        var labels = options
            .Select(o => string.IsNullOrWhiteSpace(o.Desc) ? o.Label : $"{o.Label}  ({o.Desc})")
            .ToList();
        var otherIndex = labels.Count;
        labels.Add(L10n.Get("ask.otherOption"));

        var pick = SelectList.Prompt(string.Empty, labels);
        if (pick < 0)
        {
            yield return new ToolOutput("User dismissed the question without selecting. Proceed or ask again.");
            yield break;
        }

        if (pick == otherIndex)
        {
            // '직접 입력' 선택 → 자유 텍스트를 받아 모델에 그대로 전달.
            var typed = ReadFreeText();
            if (string.IsNullOrWhiteSpace(typed))
            {
                yield return new ToolOutput("User chose to answer freely but entered nothing. Proceed or ask again.");
                yield break;
            }

            yield return new ToolOutput($"User answered (free text, not one of the listed options): {typed.Trim()}");
            yield break;
        }

        yield return new ToolOutput($"User selected option {pick + 1}: {options[pick].Label}");
    }

    // '직접 입력' 선택 시 한 줄 자유 텍스트를 읽는다. 콘솔 입력을 단독 점유하도록 ConsolePrompt 로 감싼다.
    private static string? ReadFreeText()
    {
        using (ConsolePrompt.Begin())
        {
            Console.Write(L10n.Get("ask.inputPrompt"));
            return Console.ReadLine();
        }
    }

    private static List<(string Label, string? Desc)> ParseOptions(JsonElement input)
    {
        var result = new List<(string, string?)>();
        if (!input.TryGetProperty("options", out var opts) || opts.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var el in opts.EnumerateArray())
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.String:
                    var s = el.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        result.Add((s!, null));
                    }

                    break;
                case JsonValueKind.Object:
                    var label = GetStr(el, "label");
                    if (!string.IsNullOrWhiteSpace(label))
                    {
                        result.Add((label!, GetStr(el, "description")));
                    }

                    break;
            }
        }

        return result;
    }

    private static string? GetStr(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
