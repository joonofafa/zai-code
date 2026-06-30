using System.Text.RegularExpressions;

namespace MoaiCode.Core.Agent;

/// <summary>
/// 모델이 content 에 섞어 내보내는 추론(&lt;think&gt;...&lt;/think&gt;)을 최종 텍스트에서 제거한다.
/// 일부 reasoning 모델은 여는 태그 없이 추론 후 &lt;/think&gt;만 내보내므로(orphan close),
/// 스트리밍 도중엔 사후 제거가 불가 → 텍스트를 다 모은 시점(엔진 저장/렌더)에 적용한다.
/// </summary>
public static class ThinkFilter
{
    private static readonly Regex Pair =
        new(@"<think\b[^>]*>.*?</think\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 시작부터 마지막 </think> 까지 (greedy) — orphan close: 그 앞은 전부 추론.
    private static readonly Regex OrphanClose =
        new(@"^.*</think\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 첫 <think> 부터 끝까지 — orphan open: 닫히지 않은 추론 꼬리.
    private static readonly Regex OrphanOpen =
        new(@"<think\b[^>]*>.*$", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 일부 reasoning 모델(예: Codex 중계)이 본문에 섞어 내보내는 추론 상태 마커.
    // 실제 raw 는 "__THINKING_STATUS__:<문구>" (밑줄 2개) 형태로 들어오며, 마크다운 렌더 시 __가
    // 굵게 처리되어 "THINKING_STATUS:" 처럼 보였다. 마커는 __/** 래핑 변형을 모두 허용한다.
    private const string Marker = @"(?:_{1,2}|\*{1,2})?THINKING_STATUS(?:_{1,2}|\*{1,2})?:";

    // (1) 마커 + 진행 상태 문구("Analyzing ... ...", "Reasoning... (Ns)") → 통째로 제거(순수 노이즈).
    private static readonly Regex ThinkingStatusNoise = new(
        Marker + @"[ \t]*(?:Analyzing[^\n]*?\.\.\.|Reasoning\.\.\.(?:[ \t]*\(\d+s\))?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // (2) 남은 마커 토큰만 제거 — 마커 뒤에 붙은 '실제 답변'은 보존한다.
    private static readonly Regex ThinkingStatusMarker = new(
        Marker + @"[ \t]*", RegexOptions.Compiled);

    public static string Strip(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        if (text.IndexOf("THINKING_STATUS", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            text = ThinkingStatusNoise.Replace(text, "");
            text = ThinkingStatusMarker.Replace(text, "");
        }

        if (text.IndexOf("think", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return text.Trim(); // 빠른 경로: <think> 흔적 없음 (마커는 위에서 이미 제거)
        }

        text = Pair.Replace(text, "");

        if (text.Contains("</think", StringComparison.OrdinalIgnoreCase))
        {
            text = OrphanClose.Replace(text, "");
        }

        if (text.Contains("<think", StringComparison.OrdinalIgnoreCase))
        {
            text = OrphanOpen.Replace(text, "");
        }

        return text.Trim();
    }
}
