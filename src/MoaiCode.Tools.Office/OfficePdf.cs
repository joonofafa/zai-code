namespace MoaiCode.Tools.Office;

// 열린 Office 문서를 PDF로 내보낼 때 출력 경로를 결정한다.
// path 지정 시 그 경로(.pdf 로 정규화), 없으면 원본 문서 경로/이름에서 파생.
internal static class OfficePdf
{
    public static string Resolve(string? path, string? docFullName, string workingDir)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            var p = System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(workingDir, path);
            return System.IO.Path.ChangeExtension(p, ".pdf");
        }

        // 원본 문서 경로/이름에서 파생(저장 안 된 문서는 FullName 이 "Document1" 처럼 이름만 옴).
        if (!string.IsNullOrWhiteSpace(docFullName))
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(docFullName);
                var baseName = System.IO.Path.GetFileNameWithoutExtension(docFullName);
                if (!string.IsNullOrEmpty(baseName))
                {
                    return !string.IsNullOrEmpty(dir)
                        ? System.IO.Path.Combine(dir, baseName + ".pdf")
                        : System.IO.Path.Combine(workingDir, baseName + ".pdf");
                }
            }
            catch
            {
                // 경로 파싱 실패 시 기본 이름으로.
            }
        }

        return System.IO.Path.Combine(workingDir, "export.pdf");
    }
}
