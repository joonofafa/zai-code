using System.Diagnostics;

namespace MoaiCode.Tools;

/// <summary>
/// 파일 트리 안전 순회 (Grep/Glob 공용). 무한 행(hang)/무한 루프 방지 장치를 한곳에 모은다:
/// (1) 심볼릭 링크/리파스 포인트 디렉토리 스킵 → 순환 링크로 인한 무한 루프 차단,
/// (2) 빌드/캐시 디렉토리 스킵, (3) 벽시계 예산 초과 시 중단(부분 결과), (4) 취소 토큰 준수.
/// </summary>
public sealed class FileWalker
{
    public static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg",
        "node_modules", "bin", "obj", ".vs", ".idea", "dist",
        ".venv", "venv", "__pycache__", ".tox", ".mypy_cache", ".pytest_cache",
        ".next", "target", "vendor", ".gradle", ".cache", ".terraform",
    };

    private readonly TimeSpan _budget;

    public FileWalker(TimeSpan budget)
    {
        _budget = budget;
    }

    /// <summary>예산(시간) 초과로 순회를 중단했으면 true.</summary>
    public bool TruncatedByTime { get; private set; }

    public IEnumerable<string> Walk(string root, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        if (File.Exists(root))
        {
            yield return root;
            yield break;
        }

        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (sw.Elapsed > _budget)
            {
                TruncatedByTime = true;
                yield break;
            }

            var dir = stack.Pop();

            string[] subdirs;
            string[] files;
            try
            {
                subdirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                // DirectoryNotFoundException 포함 (IOException 하위).
                continue;
            }

            foreach (var f in files)
            {
                yield return f;
            }

            foreach (var sd in subdirs)
            {
                if (SkipDirs.Contains(Path.GetFileName(sd)))
                {
                    continue;
                }

                // 심볼릭 링크/리파스 포인트 디렉토리는 따라가지 않는다 — 부모를 가리키는 순환 링크가
                // 있으면 스택이 무한히 커져 영원히 도는 버그를 차단(Docker/Python 프로젝트에서 흔함).
                try
                {
                    if ((File.GetAttributes(sd) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                stack.Push(sd);
            }
        }
    }
}
