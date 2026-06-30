namespace MoaiCode.Mcp.Skills;

/// <summary>
/// 최소 YAML 프론트매터 파서 (--- 펜스 사이의 key: value). 의존성 회피 목적.
/// 중첩/리스트 미지원 — 스킬 메타(name/description)에 충분.
/// </summary>
public static class FrontmatterParser
{
    public static (IReadOnlyDictionary<string, string> Meta, string Body) Parse(string content)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var normalized = content.Replace("\r\n", "\n");

        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            return (meta, content);
        }

        var end = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0)
        {
            return (meta, content);
        }

        var fmBlock = normalized[4..end];
        foreach (var rawLine in fmBlock.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim().Trim('"', '\'');
            meta[key] = value;
        }

        // body는 닫는 --- 다음 줄부터
        var bodyStart = normalized.IndexOf('\n', end + 1);
        var body = bodyStart < 0 ? "" : normalized[(bodyStart + 1)..].TrimStart('\n');
        return (meta, body);
    }
}
