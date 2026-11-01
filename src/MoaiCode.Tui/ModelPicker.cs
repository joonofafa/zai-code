using MoaiCode.Localization;
using MoaiCode.Tui.Input;
using Spectre.Console;

namespace MoaiCode.Tui;

/// <summary>
/// 선택·입력 프롬프트 묶음. /provider·/model 흐름이 이것만 거쳐 사용자와 주고받으므로 테스트가 각본으로 대신할 수 있다.
/// </summary>
/// <param name="Select">제목·항목·기본 위치 → 고른 위치(취소 -1).</param>
/// <param name="ReadSecret">마스킹 입력(취소 null).</param>
/// <param name="ReadLine">한 줄 입력(취소 null).</param>
/// <param name="Info">안내 한 줄.</param>
public sealed record PromptIo(
    Func<string, IReadOnlyList<string>, int, int> Select,
    Func<string, string?> ReadSecret,
    Func<string, string?> ReadLine,
    Action<string> Info)
{
    public static PromptIo Terminal { get; } = new(
        (title, items, defaultIndex) => SelectList.Prompt(title, items, defaultIndex),
        SecretPrompt.Read,
        label =>
        {
            AnsiConsole.Markup(Markup.Escape(label));
            return InputCompat.ReadLine();
        },
        message => AnsiConsole.MarkupLine($"[{TuiTheme.Dim}]{Markup.Escape(message)}[/]"));
}

/// <summary>모델 선택 공용 로직(/model·/provider 가 공유). 목록이 길면 먼저 검색어로 거른다.</summary>
public static class ModelPicker
{
    // 이보다 많으면 선택 목록 전에 검색어를 묻는다(OpenRouter 는 수백 개 — 12줄 창으로 넘기기엔 너무 길다).
    internal const int FilterThreshold = 30;

    /// <summary>대화 모델 하나를 고른다. 취소하면 null.</summary>
    public static string? Pick(PromptIo io, IReadOnlyList<string> models, string? current)
    {
        var list = Narrow(io, models, "slash.model.filterPrompt");
        if (list is null || list.Count == 0)
        {
            return null;
        }

        var def = IndexOf(list, current);
        var pick = io.Select(L10n.Get("slash.model.pickTitle"), list, def < 0 ? 0 : def);
        return pick < 0 ? null : list[pick];
    }

    /// <summary>
    /// 이미지 분석 모델을 고른다. 첫 항목은 "사용 안 함". 반환: null = 취소, "" = 사용 안 함, 그 외 모델 id.
    /// 지금 쓰는 비전 모델이 목록에 없으면(z.ai 코딩 엔드포인트 목록에 빠진 비전 모델 등) 앞에 넣어 기본으로 둔다 —
    /// 안 그러면 Enter 한 번에 "사용 안 함"이 골라져 이미지 분석이 꺼진다.
    /// </summary>
    public static string? PickVision(PromptIo io, IReadOnlyList<string> models, string? current)
    {
        var candidates = models.ToList();
        if (!string.IsNullOrEmpty(current) && IndexOf(candidates, current) < 0)
        {
            candidates.Insert(0, current);
        }

        var narrowed = Narrow(io, candidates, "slash.model.visionFilterPrompt");
        if (narrowed is null)
        {
            return null;
        }

        var items = new List<string> { L10n.Get("slash.model.visionNone") };
        items.AddRange(narrowed);
        var def = string.IsNullOrEmpty(current) ? 0 : IndexOf(narrowed, current) + 1;
        var pick = io.Select(L10n.Get("slash.model.visionPickTitle"), items, def < 0 ? 0 : def);
        return pick switch
        {
            < 0 => null,
            0 => string.Empty,
            _ => narrowed[pick - 1],
        };
    }

    // 길면 검색어로 거른다. 빈 입력은 전체, 맞는 게 없으면 다시 묻는다. 취소는 null.
    private static IReadOnlyList<string>? Narrow(PromptIo io, IReadOnlyList<string> models, string promptKey)
    {
        while (models.Count > FilterThreshold)
        {
            var query = io.ReadLine(L10n.Get(promptKey, models.Count))?.Trim();
            if (query is null)
            {
                return null;
            }

            if (query.Length == 0)
            {
                return models;
            }

            var hits = models.Where(m => m.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count > 0)
            {
                return hits;
            }

            io.Info(L10n.Get("slash.model.filterNoMatch", query));
        }

        return models;
    }

    private static int IndexOf(IReadOnlyList<string> list, string? value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
