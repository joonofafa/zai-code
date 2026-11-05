using System.Text;
using System.Text.RegularExpressions;
using MoaiCode.Tools;

namespace MoaiCode.Cli;

public static class RepoMapBuilder
{
    private static readonly string[] Extensions =
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".go", ".rs", ".java", ".kt",
    };

    // 리포맵은 시작 경로에서 동기로 만들어지므로(첫 응답이 이만큼 늦어진다) 예산을 짧게 잡는다.
    // 초과하면 그때까지 모은 파일로 맵을 만든다 — 없는 것보다 부분 맵이 낫다.
    private static readonly TimeSpan ScanBudget = TimeSpan.FromSeconds(3);

    private static readonly Regex Symbol = new(
        @"^\s*(public|private|protected|internal|export|async|static|sealed|abstract|partial|\s)*\s*(class|interface|record|struct|enum|def|function|func|fn|type)\s+[A-Za-z_][\w<>,]*",
        RegexOptions.Compiled);

    /// <summary>
    /// 시작 폴더의 코드 지도. git 저장소 안에서만 만든다 — 홈 폴더처럼 프로젝트가 아닌 곳에서 켜면
    /// 홈 전체(SDK·다른 저장소들)를 훑다 예산(3초)을 다 쓰고 쓸모없는 지도만 남기기 때문이다.
    /// </summary>
    public static string? Build(string root, int tokenBudget)
    {
        if (tokenBudget <= 0 || !Directory.Exists(root) || !InsideGitRepo(root))
        {
            return null;
        }

        var charBudget = tokenBudget * 4;
        var files = EnumerateSourceFiles(root)
            .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => Relative(root, f), StringComparer.Ordinal)
            .Take(400);

        var sb = new StringBuilder();
        foreach (var file in files)
        {
            var symbols = ReadSymbols(file).Take(12).ToList();
            if (symbols.Count == 0)
            {
                continue;
            }

            var rel = Relative(root, file);
            var block = new StringBuilder();
            block.AppendLine(rel + ":");
            foreach (var (line, text) in symbols)
            {
                block.Append("  L").Append(line).Append(": ").AppendLine(text);
            }

            if (sb.Length + block.Length > charBudget)
            {
                break;
            }

            sb.Append(block);
        }

        return sb.Length == 0 ? null : sb.ToString().TrimEnd();
    }

    private static IEnumerable<(int Line, string Text)> ReadSymbols(string file)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(file);
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length > 160)
            {
                trimmed = trimmed[..160];
            }

            if (Symbol.IsMatch(trimmed))
            {
                yield return (i + 1, trimmed);
            }
        }
    }

    // 순회는 FileWalker 에 맡긴다 — 심볼릭 링크 스킵(홈에 걸린 네트워크 마운트로 새어나가면
    // 끝나지 않는다)·빌드 디렉토리 가지치기·시간 예산이 거기 모여 있다. 여기서 다시 구현하지 말 것.
    private static IEnumerable<string> EnumerateSourceFiles(string root)
        => new FileWalker(ScanBudget).Walk(root, CancellationToken.None);

    // root 또는 그 상위에 .git(폴더, 워크트리·서브모듈은 파일)이 있으면 git 저장소 안이다.
    private static bool InsideGitRepo(string root)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(root)); dir is not null; dir = dir.Parent)
        {
            var git = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                return true;
            }
        }

        return false;
    }

    private static string Relative(string root, string file)
    {
        try
        {
            return Path.GetRelativePath(root, file);
        }
        catch
        {
            return file;
        }
    }
}
