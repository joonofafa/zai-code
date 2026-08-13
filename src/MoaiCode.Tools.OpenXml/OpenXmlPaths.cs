using MoaiCode.Localization;

namespace MoaiCode.Tools.OpenXml;

/// <summary>출력 경로를 워크스페이스 기준으로 해석하고 확장자를 검증한다.</summary>
internal static class OpenXmlPaths
{
    /// <summary>path 를 워크스페이스 기준 절대경로로. 상위 디렉토리는 생성. 확장자 불일치면 예외.</summary>
    public static string ResolveForWrite(string workingDirectory, string path, string expectedExt)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(L10n.Get("tools.openXmlPaths.pathRequired"));
        }

        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workingDirectory, path));
        if (!full.EndsWith(expectedExt, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(L10n.Get("tools.openXmlPaths.badExtension", expectedExt, path));
        }

        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        return full;
    }

    /// <summary>읽기용 경로 해석. 존재하지 않으면 예외.</summary>
    public static string ResolveForRead(string workingDirectory, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(L10n.Get("tools.openXmlPaths.pathRequired"));
        }

        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workingDirectory, path));
        if (!File.Exists(full))
        {
            throw new FileNotFoundException(L10n.Get("tools.openXmlPaths.fileNotFound", path));
        }

        return full;
    }
}
