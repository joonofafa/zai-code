using System.Text;

namespace MoaiCode.Tui;

/// <summary>
/// 편집 커서가 한 번에 건너는 글자 길이(UTF-16 단위). 서로게이트 쌍(이모지 등)은 2 — 한 단위씩 움직이면
/// Backspace·Delete 가 반쪽만 지우고 ←/→ 가 쌍 가운데로 들어가 깨진 글자(�)가 제출됐다. 입력창(BottomDock)과
/// 줄 편집기(LineEditor)가 같이 쓴다.
/// </summary>
internal static class TextCursor
{
    /// <summary>pos 바로 앞 글자의 길이.</summary>
    public static int PrevLength(StringBuilder buf, int pos) =>
        pos >= 2 && char.IsLowSurrogate(buf[pos - 1]) && char.IsHighSurrogate(buf[pos - 2]) ? 2 : 1;

    /// <summary>pos 의 글자 길이.</summary>
    public static int NextLength(StringBuilder buf, int pos) =>
        pos + 1 < buf.Length && char.IsHighSurrogate(buf[pos]) && char.IsLowSurrogate(buf[pos + 1]) ? 2 : 1;
}
