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

        // 글자 경로와 링크를 푼 실경로를 둘 다 본다 — 워크스페이스 안의 링크(ws/out -> $HOME)를 거치면
        // 글자로는 안쪽이라 '밖 쓰기' 확인이 뜨지 않았다.
        return !IsUnder(full, ws) || !IsUnder(ResolveLinks(full), ResolveLinks(ws));
    }

    private static bool IsUnder(string path, string dir)
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        dir = dir.TrimEnd(Path.DirectorySeparatorChar, '/');
        var norm = path.TrimEnd(Path.DirectorySeparatorChar, '/');
        return norm.Equals(dir, cmp) || norm.StartsWith(dir + Path.DirectorySeparatorChar, cmp);
    }

    /// <summary>
    /// 경로의 심볼릭 링크(디렉터리 링크·정션 포함)를 앞에서부터 풀어 실경로를 만든다. 아직 없는 꼬리는 그대로 붙인다.
    /// Path.GetFullPath 는 링크를 따라가지 않는다. 순환 링크는 횟수 상한에서 멈춘다(그때까지 푼 경로).
    /// </summary>
    internal static string ResolveLinks(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var parts = fullPath[root.Length..].Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        var hops = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var next = Path.Combine(current, parts[i]);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (info.LinkTarget is not null && hops++ < 40)
            {
                try
                {
                    var target = info.ResolveLinkTarget(returnFinalTarget: true);
                    if (target is not null)
                    {
                        next = Path.GetFullPath(target.FullName);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 끊긴 링크·순환·권한 없음 — 글자 경로로 둔다
                }
            }
            else if (!info.Exists)
            {
                // 이 아래는 아직 없다 — 나머지를 그대로 붙인다.
                return Path.Combine(new[] { next }.Concat(parts[(i + 1)..]).ToArray());
            }

            current = next;
        }

        return current;
    }

    /// <summary>쓰기/수정 거부 사유. null 이면 허용. 링크를 푼 실경로도 검사한다(ws/sys -> /etc 우회 방지).</summary>
    public static string? DenyWriteReason(string fullPath)
    {
        var reason = DenyWriteReasonLiteral(fullPath);
        if (reason is not null)
        {
            return reason;
        }

        string real;
        try
        {
            real = ResolveLinks(Path.GetFullPath(fullPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;   // 글자 경로는 이미 통과 — 링크 해석 실패만으로 막지는 않는다
        }

        return DenyWriteReasonLiteral(real);
    }

    private static string? DenyWriteReasonLiteral(string fullPath)
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
