namespace MoaiCode.Tui;

/// <summary>
/// 스킬 활성/비활성 멀티셀렉트 공용 플로우. /skills 명령과 로그인 직후 스킬 선택이 공유한다.
/// 저장 방식은 호출측이 정한다(/skills=라이브 재적재+메시지, 로그인=SkillState 저장).
/// 호출측이 목록 유무·대화형 여부를 먼저 판단한다(빈 목록/비대화형이면 호출하지 않음).
/// </summary>
public static class SkillPicker
{
    /// <param name="title">피커 제목.</param>
    /// <param name="choices">전체 스킬 (이름·출처·현재 활성 여부).</param>
    /// <returns>비활성으로 둘 스킬 이름 목록(확정 시). Esc(건너뜀)면 null.</returns>
    public static IReadOnlyList<string>? Run(
        string title,
        IReadOnlyList<(string Name, string Source, bool Enabled)> choices)
    {
        var labels = choices.Select(c => $"{c.Name}  ({c.Source})").ToList();
        var initial = choices.Select(c => c.Enabled).ToList();
        var picked = MultiSelectList.Prompt(title, labels, initial);
        if (picked is null)
        {
            return null; // Esc — 변경 없음
        }

        var on = picked.ToHashSet();
        return choices.Where((c, i) => !on.Contains(i)).Select(c => c.Name).ToList();
    }
}
