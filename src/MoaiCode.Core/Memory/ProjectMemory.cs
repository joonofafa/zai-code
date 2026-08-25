using System.Text;

namespace MoaiCode.Core.Memory;

/// <summary>
/// 프로젝트별 저장소 — 세션 간에 유지되는 '배운 사실'과 대화 세션을 cwd 단위로 갈라 담는다.
/// 위치: ~/.zaicode/projects/&lt;cwd-slug&gt;/{memory,sessions}/ (저장소 밖 — 커밋되지 않음).
/// 키는 **cwd 절대경로**를 percent-encoding 한 슬러그다(PathSlug 참조). git 탐색을 쓰지 않으므로
/// $HOME 이 git repo 여도 홈 아래 전체가 한 슬러그로 뭉개지지 않는다.
/// 각 사실은 frontmatter + 본문의 단일 .md 파일이고, MEMORY.md 인덱스는 매 세션 시스템 프롬프트에 주입된다.
/// 모든 연산은 best-effort(실패해도 예외를 삼킴) — 메모리 장애가 세션을 막지 않게 한다.
/// </summary>
public static class ProjectMemory
{
    private const int IndexCap = 6000;

    /// <summary>이 프로젝트(cwd)의 메모리 디렉터리 절대경로.</summary>
    public static string Dir(string cwd) => Path.Combine(ProjectDir(cwd), "memory");

    /// <summary>이 프로젝트(cwd)의 세션 디렉터리 절대경로. /resume 가 현재 프로젝트만 보게 한다.</summary>
    public static string SessionsDir(string cwd) => Path.Combine(ProjectDir(cwd), "sessions");

    /// <summary>~/.zaicode/projects/&lt;slug&gt; — memory/ 와 sessions/ 의 공통 부모.</summary>
    private static string ProjectDir(string cwd)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = Path.Combine(home, ".zaicode", "projects", PathSlug(cwd));
        MigrateLegacyOnce(cwd, home, dir);
        return dir;
    }

    // 슬러그 규칙이 바뀌어 기존 메모리가 고아가 되는 것을 막는 1회성 이관.
    // cwd 가 '옛 규칙의 프로젝트 루트' 였을 때만 옮긴다 — 하위 디렉토리/홈 붕괴 케이스까지 옮기면
    // 남의 프로젝트 메모리를 가져오게 된다. 실패는 non-fatal(새로 시작하면 그만).
    private static void MigrateLegacyOnce(string cwd, string home, string newDir)
    {
        try
        {
            if (Directory.Exists(newDir))
            {
                return;
            }

            var full = Path.GetFullPath(cwd);
            if (!string.Equals(LegacyProjectRoot(cwd), full, StringComparison.Ordinal))
            {
                return;
            }

            var oldDir = Path.Combine(home, ".zaicode", "projects", LegacyPathSlug(full));
            if (!Directory.Exists(oldDir) || string.Equals(oldDir, newDir, StringComparison.Ordinal))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(newDir)!);
            Directory.Move(oldDir, newDir);
        }
        catch
        {
            // best-effort: 이관 실패해도 새 위치에서 새로 시작한다.
        }
    }

    public static string IndexPath(string cwd) => Path.Combine(Dir(cwd), "MEMORY.md");

    /// <summary>MEMORY.md 인덱스 텍스트(없으면 null). 길이 상한 적용.</summary>
    public static string? LoadIndex(string cwd)
    {
        try
        {
            var p = IndexPath(cwd);
            if (!File.Exists(p))
            {
                return null;
            }

            var text = File.ReadAllText(p).Trim();
            if (text.Length == 0)
            {
                return null;
            }

            return text.Length > IndexCap ? text[..IndexCap] : text;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>사실 하나를 저장/갱신(같은 name이면 덮어씀) 후 인덱스 재생성.</summary>
    public static string Save(string cwd, string name, string? description, string? type, string content)
    {
        var slug = NameSlug(name);
        if (slug.Length == 0)
        {
            return "Memory: 'name' is required.";
        }

        var dir = Dir(cwd);
        Directory.CreateDirectory(dir);
        var t = NormalizeType(type);
        var desc = (description ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
        var body = (content ?? string.Empty).Trim();

        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("name: ").Append(slug).Append('\n');
        sb.Append("description: ").Append(desc).Append('\n');
        sb.Append("metadata:\n  type: ").Append(t).Append('\n');
        sb.Append("---\n\n");
        sb.Append(body).Append('\n');

        File.WriteAllText(Path.Combine(dir, slug + ".md"), sb.ToString());
        RebuildIndex(dir);
        return $"Saved memory '{slug}' ({t}).";
    }

    /// <summary>name으로 사실 삭제 후 인덱스 재생성.</summary>
    public static string Delete(string cwd, string name)
    {
        var slug = NameSlug(name);
        var dir = Dir(cwd);
        var f = Path.Combine(dir, slug + ".md");
        if (!File.Exists(f))
        {
            return $"Memory '{slug}' not found.";
        }

        File.Delete(f);
        RebuildIndex(dir);
        return $"Deleted memory '{slug}'.";
    }

    /// <summary>저장된 사실 목록(name — description).</summary>
    public static string List(string cwd)
    {
        var dir = Dir(cwd);
        if (!Directory.Exists(dir))
        {
            return "No memories yet.";
        }

        var files = FactFiles(dir);
        if (files.Count == 0)
        {
            return "No memories yet.";
        }

        var sb = new StringBuilder();
        foreach (var f in files)
        {
            var (n, d) = ReadFront(f);
            sb.Append("- ").Append(n);
            if (d.Length > 0)
            {
                sb.Append(" — ").Append(d);
            }

            sb.Append('\n');
        }

        return sb.ToString().TrimEnd();
    }

    // ── 내부 ─────────────────────────────────────────────────────────────
    private static List<string> FactFiles(string dir) =>
        Directory.GetFiles(dir, "*.md")
            .Where(f => !string.Equals(Path.GetFileName(f), "MEMORY.md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

    private static void RebuildIndex(string dir)
    {
        try
        {
            var files = FactFiles(dir);
            var sb = new StringBuilder();
            sb.Append("# Memory Index\n\n");
            if (files.Count == 0)
            {
                sb.Append("(empty)\n");
            }
            else
            {
                foreach (var f in files)
                {
                    var (n, d) = ReadFront(f);
                    sb.Append("- [").Append(n).Append("](").Append(Path.GetFileName(f)).Append(')');
                    if (d.Length > 0)
                    {
                        sb.Append(" — ").Append(d);
                    }

                    sb.Append('\n');
                }
            }

            File.WriteAllText(Path.Combine(dir, "MEMORY.md"), sb.ToString());
        }
        catch
        {
            // best-effort: 인덱스 재생성 실패는 사실 파일 자체엔 영향 없음.
        }
    }

    private static (string Name, string Desc) ReadFront(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var desc = string.Empty;
        try
        {
            foreach (var raw in File.ReadLines(path).Take(8))
            {
                var line = raw.Trim();
                if (line.StartsWith("name:", StringComparison.Ordinal))
                {
                    var v = line[5..].Trim();
                    if (v.Length > 0)
                    {
                        name = v;
                    }
                }
                else if (line.StartsWith("description:", StringComparison.Ordinal))
                {
                    desc = line[12..].Trim();
                }
            }
        }
        catch
        {
            // 읽기 실패 시 파일명만 사용.
        }

        return (name, desc);
    }

    private static string NormalizeType(string? t)
    {
        t = (t ?? string.Empty).Trim().ToLowerInvariant();
        return t is "user" or "feedback" or "project" or "reference" ? t : "project";
    }

    // 메모리 name → 파일 슬러그(소문자 영숫자 + 대시, 중복 대시 축약).
    private static string NameSlug(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in (name ?? string.Empty).Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
            else if (ch is ' ' or '_' or '-')
            {
                sb.Append('-');
            }
        }

        var s = sb.ToString();
        while (s.Contains("--", StringComparison.Ordinal))
        {
            s = s.Replace("--", "-");
        }

        return s.Trim('-');
    }

    // [옛 규칙 — 이관 판정 전용] cwd가 속한 git repo 루트(.git 을 만날 때까지 상위로). git 밖이면 cwd 자체.
    // $HOME 이 repo 면 홈 아래 전부가 한 루트로 붕괴하는 문제가 있어 현재 경로 계산엔 쓰지 않는다.
    private static string LegacyProjectRoot(string cwd)
    {
        try
        {
            var dir = new DirectoryInfo(Path.GetFullPath(cwd));
            while (dir is not null)
            {
                var gitPath = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(gitPath) || File.Exists(gitPath))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }
        }
        catch
        {
            // 탐색 실패 시 cwd 폴백.
        }

        try
        {
            return Path.GetFullPath(cwd);
        }
        catch
        {
            return cwd;
        }
    }

    // 절대경로 → 프로젝트 슬러그. URI 인코딩과 동형이라 서로 다른 경로가 절대 겹치지 않는다(단사):
    //   '/' → '-' (구분자, 가독성)   [A-Za-z0-9_.] → 그대로   그 외 바이트 → %XX (대문자 hex)
    // '-' 자신도 %2D 로 이스케이프되므로 '-' 는 오직 구분자만 의미한다.
    //   예) /x/moai-code → -x-moai%2Dcode  vs  /x/moai/code → -x-moai-code
    // 되돌릴 필요는 없다(항상 cwd 에서 새로 계산) — 유일성만 보장하면 된다.
    private static string PathSlug(string cwd)
    {
        string full;
        try
        {
            full = Path.GetFullPath(cwd);
        }
        catch
        {
            full = cwd;
        }

        var sb = new StringBuilder(full.Length + 8);
        foreach (var b in Encoding.UTF8.GetBytes(full))
        {
            if (b == (byte)'/')
            {
                sb.Append('-');
            }
            else if ((b >= (byte)'A' && b <= (byte)'Z')
                     || (b >= (byte)'a' && b <= (byte)'z')
                     || (b >= (byte)'0' && b <= (byte)'9')
                     || b == (byte)'_' || b == (byte)'.')
            {
                sb.Append((char)b);
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2"));
            }
        }

        return sb.ToString();
    }

    // [옛 규칙 — 이관 판정 전용] 영숫자 외 전부 '-'. lossy 라 서로 다른 경로가 충돌할 수 있었다.
    private static string LegacyPathSlug(string full)
    {
        var sb = new StringBuilder(full.Length);
        foreach (var ch in full)
        {
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        }

        return sb.ToString();
    }
}
