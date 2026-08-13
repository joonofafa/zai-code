using System.Text;
using MoaiCode.Localization;

namespace MoaiCode.Tui;

/// <summary>
/// 모델 선택 공용 플로우: "모델별 업무 분할(Y/n)" → 예=하/중/상 티어 선택, 아니오=단일 모델.
/// /model 명령과 로그인 화면이 공유한다. 저장 방식은 persist 콜백으로 주입한다
/// (라이브 전환+settings 영속 vs 로그인 시 settings 쓰기).
/// </summary>
public static class ModelPicker
{
    /// <param name="models">선택 가능한 모델 목록(이미 조회됨).</param>
    /// <param name="currentModel">단일 모드에서 기본 강조할 현재 모델.</param>
    /// <param name="persistSingle">단일 모델 확정 콜백.</param>
    /// <param name="persistTier">티어(low/mid/high) 확정 콜백. model=null 이면 해제(기본 모델 사용).</param>
    /// <param name="currentTier">각 티어의 현재 모델 조회(기본 강조용). null 이면 강조 없음(로그인 등 신규).</param>
    /// <returns>사용자에게 보여줄 요약(로컬라이즈). 비대화형/취소 시 안내 문자열.</returns>
    public static string Run(
        IReadOnlyList<string> models,
        string? currentModel,
        Action<string> persistSingle,
        Action<string, string?> persistTier,
        Func<string, string?>? currentTier = null)
    {
        if (models.Count == 0 || Console.IsInputRedirected)
        {
            return string.Empty;
        }

        var split = SelectList.Prompt(
            L10n.Get("slash.model.splitPrompt"),
            new[] { L10n.Get("slash.model.splitYes"), L10n.Get("slash.model.splitNo") },
            0);
        if (split < 0)
        {
            return L10n.Get("common.unchanged");
        }

        if (split == 0)
        {
            // 하급 → 중급 → 고급 순으로 각 티어 모델 선택. 취소(Esc)면 그 티어는 변경하지 않는다.
            var summary = new StringBuilder(L10n.Get("slash.model.tierSummary") + "\n");
            foreach (var (labelKey, tier) in new[]
                     { ("slash.model.tierLow", "low"), ("slash.model.tierMid", "mid"), ("slash.model.tierHigh", "high") })
            {
                var label = L10n.Get(labelKey);
                var opts = new List<string> { L10n.Get("slash.model.tierClearOption") };
                opts.AddRange(models);
                var cur = currentTier?.Invoke(tier);
                var def = string.IsNullOrEmpty(cur) ? 0 : Math.Max(0, opts.FindIndex(o => string.Equals(o, cur, StringComparison.OrdinalIgnoreCase)));
                var p = SelectList.Prompt(L10n.Get("slash.model.tierPickTitle", label, tier), opts, def);
                if (p < 0)
                {
                    summary.AppendLine($"  {label}: {L10n.Get("slash.model.tierUnchanged")}");
                    continue;
                }

                var chosen = p == 0 ? null : opts[p];
                persistTier(tier, chosen);
                summary.AppendLine($"  {label}: {chosen ?? L10n.Get("slash.model.tierDefault")}");
            }

            return summary.ToString().TrimEnd();
        }

        var defIdx = models.ToList().FindIndex(m => string.Equals(m, currentModel, StringComparison.OrdinalIgnoreCase));
        var pick = SelectList.Prompt(L10n.Get("slash.model.pickTitle"), models, defIdx < 0 ? 0 : defIdx);
        if (pick < 0)
        {
            return L10n.Get("common.unchanged");
        }

        persistSingle(models[pick]);
        return L10n.Get("slash.model.changed", models[pick]);
    }
}
