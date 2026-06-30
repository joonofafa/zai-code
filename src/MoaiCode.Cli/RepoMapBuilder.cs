using System.Text;
using System.Text.RegularExpressions;

namespace MoaiCode.Cli;

public static class RepoMapBuilder
{
    private static readonly string[] Extensions =
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".go", ".rs", ".java", ".kt",
    };

    private static readonly string[] SkipDirs =
    {
        ".git", "bin", "obj", "node_modules", "dist", "build", ".next", ".cache", "coverage",
    };

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

    // SkipDirs를 트래버설 단계에서 가지치기 — node_modules/.git 등 대형 디렉토리로 아예 내려가지 않는다.
    // (EnumerateFiles(AllDirectories)는 필터링 전에 전부 순회하므로 큰 레포에서 느림.)
    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] subDirs;
            string[] files;
            try
            {
                subDirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var sub in subDirs)
            {
                var name = Path.GetFileName(sub);
                if (!SkipDirs.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    stack.Push(sub);
                }
            }

            foreach (var f in files)
            {
                yield return f;
            }
        }
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
