using System;
using System.Runtime.Versioning;
using MoaiCode.Localization;

namespace MoaiCode.Tools.Office;

/// <summary>
/// Word/Excel/PowerPoint 공통 도형 기하 편집. 세 앱이 같은 Office Shape 모델
/// (Left/Top/Width/Height/Rotation/Flip)을 공유하므로 한 곳에서 처리한다.
/// 값은 절대값(단위: points), 회전은 도(deg, 시계방향). 지정한 항목만 적용한다.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ShapeGeometry
{
    // MsoFlipCmd.
    private const int MsoFlipHorizontal = 0;
    private const int MsoFlipVertical = 1;

    /// <summary>flip 값이 유효하지 않으면 사용자용 메시지, 비었거나 유효하면 null.</summary>
    internal static string? ValidateFlip(string? flip) =>
        TryNormalizeFlip(flip, out _) ? null : L10n.Get("tools.shapeGeometry.invalidFlip");

    /// <summary>기하 인자 중 실제로 넣은 게 하나도 없으면 true(적용할 게 없음).</summary>
    internal static bool IsEmpty(
        double? left, double? top, double? width, double? height, double? rotation, string? flip) =>
        left is null && top is null && width is null && height is null
        && rotation is null && string.IsNullOrWhiteSpace(flip);

    /// <summary>지정된 항목만 도형에 적용하고, 적용한 항목 수를 돌려준다.</summary>
    internal static int Apply(
        dynamic shape, double? left, double? top, double? width, double? height, double? rotation, string? flip)
    {
        var n = 0;
        if (left is not null) { shape.Left = (float)left.Value; n++; }
        if (top is not null) { shape.Top = (float)top.Value; n++; }
        if (width is not null) { shape.Width = (float)width.Value; n++; }
        if (height is not null) { shape.Height = (float)height.Value; n++; }
        if (rotation is not null) { shape.Rotation = (float)rotation.Value; n++; }
        if (TryNormalizeFlip(flip, out var dir) && dir is not null) { shape.Flip(dir.Value); n++; }
        return n;
    }

    private static bool TryNormalizeFlip(string? flip, out int? dir)
    {
        if (string.IsNullOrWhiteSpace(flip))
        {
            dir = null;
            return true;
        }

        switch (flip.Trim().ToLowerInvariant())
        {
            case "horizontal" or "h" or "좌우":
                dir = MsoFlipHorizontal;
                return true;
            case "vertical" or "v" or "상하":
                dir = MsoFlipVertical;
                return true;
            default:
                dir = null;
                return false;
        }
    }
}
