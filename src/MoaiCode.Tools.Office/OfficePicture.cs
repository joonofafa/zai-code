using System;
using System.IO;
using System.Runtime.Versioning;

namespace MoaiCode.Tools.Office;

/// <summary>열린 문서에 그림(insert_picture)을 삽입할 때 공통으로 쓰는 상수·경로 해석.</summary>
[SupportedOSPlatform("windows")]
internal static class OfficePicture
{
    // Shapes.AddPicture 의 MsoTriState 인자.
    internal const int LinkToFileFalse = 0;   // msoFalse — 파일을 링크하지 않고
    internal const int SaveWithDocTrue = -1;  // msoTrue  — 문서 안에 포함 저장
    internal const float KeepNative = -1f;    // Width/Height 에 넣으면 원본 크기 유지

    /// <summary>이미지 경로를 절대경로로 만들고 존재를 확인한다. 비었거나 없으면 예외.</summary>
    internal static string ResolvePath(string? path, string workingDir)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("insert_picture 에는 path(이미지 파일 경로)가 필요합니다.");
        }

        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workingDir, path));
        if (!File.Exists(full))
        {
            throw new InvalidOperationException($"이미지 파일이 없습니다: {full}");
        }

        return full;
    }
}
