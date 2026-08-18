using MoaiCode.Localization;

namespace MoaiCode.Tui;

/// <summary>
/// 모델 선택 공용 플로우: 단일 모델 선택.
/// /model 명령과 로그인 화면이 공유한다. 저장 방식은 persist 콜백으로 주입한다
/// (라이브 전환+settings 영속 vs 로그인 시 settings 쓰기).
/// </summary>
public static class ModelPicker
{
    /// <param name="models">선택 가능한 모델 목록(이미 조회됨).</param>
    /// <param name="currentModel">단일 모드에서 기본 강조할 현재 모델.</param>
    /// <param name="persistSingle">단일 모델 확정 콜백.</param>
    /// <returns>사용자에게 보여줄 요약(로컬라이즈). 비대화형/취소 시 안내 문자열.</returns>
    public static string Run(
        IReadOnlyList<string> models,
        string? currentModel,
        Action<string> persistSingle)
    {
        if (models.Count == 0 || Console.IsInputRedirected)
        {
            return string.Empty;
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
