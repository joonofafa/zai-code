using System.Globalization;

namespace MoaiCode.Tools.Office;

/// <summary>Office COM 색 변환 공용 헬퍼. "#RRGGBB"/이름 → RGB(int, BGR 순서).</summary>
internal static class OfficeColor
{
    /// <summary>"#RRGGBB"/"RRGGBB" 또는 기본 색 이름을 Office COM 의 RGB(int, 0x00BBGGRR)로 변환.</summary>
    public static int ToBgr(string color)
    {
        var c = color.Trim();

        var named = c.ToLowerInvariant() switch
        {
            "black" or "검정" or "검정색" or "검은색" => "#000000",
            "white" or "흰색" or "하양" => "#FFFFFF",
            "red" or "빨강" or "빨간색" => "#FF0000",
            "green" or "초록" or "초록색" or "녹색" => "#008000",
            "blue" or "파랑" or "파란색" or "파랑색" => "#0000FF",
            "yellow" or "노랑" or "노란색" => "#FFFF00",
            "orange" or "주황" or "주황색" => "#FFA500",
            "purple" or "보라" or "보라색" => "#800080",
            "gray" or "grey" or "회색" => "#808080",
            "navy" or "남색" => "#000080",
            "skyblue" or "하늘색" => "#87CEEB",
            _ => c,
        };

        var hex = named.StartsWith('#') ? named[1..] : named;
        if (hex.Length == 6
            && int.TryParse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)
            && int.TryParse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
            && int.TryParse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            // Office COM 의 RGB 는 0x00BBGGRR (BGR) 순서.
            return (b << 16) | (g << 8) | r;
        }

        throw new System.InvalidOperationException(
            $"색을 해석할 수 없습니다: '{color}'. '#RRGGBB' 형식이나 기본 색 이름(red, blue, ...)을 쓰세요.");
    }
}
