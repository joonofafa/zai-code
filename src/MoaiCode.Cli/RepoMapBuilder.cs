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

    public static string? Build(string root, int tokenBudget)
    {
        if (tokenBudget <= 0 || !Directory.Exists(root))
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
