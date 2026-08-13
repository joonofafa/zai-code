using MoaiCode.Localization;

namespace MoaiCode.Tools;

/// <summary>
/// 파일 쓰기/수정의 하드 플로어. 권한 모드(ask/auto/deny)와 무관하게, 시스템 임계 경로에 대한
/// 쓰기/수정/삭제를 무조건 거부한다. 부트로더·시스템 디렉토리 파괴 같은 비가역 사고 방지.
/// (읽기는 막지 않는다 — 파괴적이지 않음.)
/// </summary>
public static class PathSafety
{
    private static readonly string[] UnixCriticalRoots =
    {
        "/boot", "/bin", "/sbin", "/lib", "/lib64", "/lib32", "/libx32",
        "/usr", "/etc", "/sys", "/proc", "/dev", "/run",
        "/var/lib", "/var/run", "/var/log",
        // macOS
        "/System", "/Library", "/private/etc", "/private/var/db",
    };

    /// <summary>
    /// path(상대/절대/~ 포함)를 workspace 기준으로 해석했을 때 워크스페이스 밖이면 true.
    /// 확인 게이트(워크스페이스 밖 쓰기 승인 강제)에서 사용. 해석 실패는 보수적으로 '밖'(true).
    /// </summary>
    public static bool IsOutsideWorkspace(string workspace, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string ws;
        string full;
        try
        {
            ws = Path.GetFullPath(workspace);
            full = ToolSchema.ResolvePath(workspace, path); // ~ 확장 + 상대경로 결합 처리
        }
        catch
        {
            return true;
        }

        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        ws = ws.TrimEnd(Path.DirectorySeparatorChar, '/');
        var norm = full.TrimEnd(Path.DirectorySeparatorChar, '/');
        return !(norm.Equals(ws, cmp) || norm.StartsWith(ws + Path.DirectorySeparatorChar, cmp));
    }

    /// <summary>쓰기/수정 거부 사유. null 이면 허용.</summary>
    public static string? DenyWriteReason(string fullPath)
    {
        string p;
        try
        {
            p = Path.GetFullPath(fullPath);
        }
        catch
        {
            return L10n.Get("tools.pathSafety.resolveFailed");
        }

        var norm = p.TrimEnd('/', '\\');
        if (norm.Length == 0)
        {
            return L10n.Get("tools.pathSafety.filesystemRoot");
        }

        // 파일시스템 루트 자체 (/, C:\).
        var root = (Path.GetPathRoot(norm) ?? string.Empty).TrimEnd('/', '\\');
        if (norm.Length <= root.Length)
        {
            return L10n.Get("tools.pathSafety.filesystemRoot");
        }

        if (OperatingSystem.IsWindows())
        {
            return DenyWindows(norm);
        }

        foreach (var critical in UnixCriticalRoots)
        {
            if (norm.Equals(critical, StringComparison.Ordinal) ||
                norm.StartsWith(critical + "/", StringComparison.Ordinal))
            {
                return L10n.Get("tools.pathSafety.criticalPathDenied", critical);
            }
        }

        return null;
    }

    private static string? DenyWindows(string norm)
    {
        var roots = new List<string>();
        void Add(Environment.SpecialFolder f)
        {
            var d = Environment.GetFolderPath(f);
            if (!string.IsNullOrEmpty(d))
            {
                roots.Add(d.TrimEnd('\\'));
            }
        }

        Add(Environment.SpecialFolder.Windows);
        Add(Environment.SpecialFolder.System);
        Add(Environment.SpecialFolder.ProgramFiles);
        Add(Environment.SpecialFolder.ProgramFilesX86);

        foreach (var r in roots)
        {
            if (norm.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                norm.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return L10n.Get("tools.pathSafety.criticalPathDenied", r);
            }
        }

        return null;
    }
}
