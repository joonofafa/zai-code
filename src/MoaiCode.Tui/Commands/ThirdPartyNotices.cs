namespace MoaiCode.Tui.Commands;

/// <summary>
/// 제3자 고지(THIRD_PARTY_NOTICES.md)를 /license 가 보여줄 수 있게 읽는다. 원본은 저장소 루트의 그 문서 하나뿐이고
/// (게시본 실행 파일 옆에도 같은 파일이 나간다) 어셈블리에 리소스로 들어 있다 — 코드에 목록을 따로 두면 문서와
/// 어긋나므로, 표와 라이선스 전문을 문서에서 그대로 꺼낸다.
/// </summary>
internal static class ThirdPartyNotices
{
    /// <summary>고지 표의 한 줄. Kind = "library" | "theme". Source 는 테마의 원작(라이브러리는 null).</summary>
    internal sealed record Entry(string Kind, string Name, string? Version, string License, string Holder, string? Source);

    private static readonly Lazy<string> Embedded = new(Load);

    /// <summary>임베드된 고지 문서 전문. 리소스가 없으면(비정상 빌드) 빈 문자열.</summary>
    public static string Text => Embedded.Value;

    private static string Load()
    {
        using var s = typeof(ThirdPartyNotices).Assembly.GetManifestResourceStream("THIRD_PARTY_NOTICES.md");
        if (s is null)
        {
            return string.Empty;
        }

        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>"## 1. Libraries…" 와 "## 2. Theme…" 절의 마크다운 표를 읽는다(머리행 이름으로 열을 찾는다).</summary>
    public static IReadOnlyList<Entry> Entries(string text)
    {
        var result = new List<Entry>();
        string? kind = null;
        string[]? header = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                kind = line.Contains("Librar", StringComparison.OrdinalIgnoreCase) ? "library"
                     : line.Contains("Theme", StringComparison.OrdinalIgnoreCase) ? "theme"
                     : null;
                header = null;
                continue;
            }

            if (kind is null || !line.StartsWith('|'))
            {
                header = line.StartsWith('|') ? header : null;
                continue;
            }

            var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (header is null)
            {
                header = cells;
                continue;
            }

            if (cells.All(c => c.Length > 0 && c.All(ch => ch is '-' or ':')))
            {
                continue;   // 구분 행(|---|---|)
            }

            string? Col(params string[] names)
            {
                var i = Array.FindIndex(header, h => names.Any(n => h.Equals(n, StringComparison.OrdinalIgnoreCase)));
                return i >= 0 && i < cells.Length && cells[i].Length > 0 ? cells[i] : null;
            }

            var name = Col("Component", "Product theme");
            var license = Col("License");
            if (name is null || license is null)
            {
                continue;
            }

            result.Add(new Entry(kind, name, Col("Version"), license, Col("Copyright holder") ?? "", Col("Derived from")));
        }

        return result;
    }

    /// <summary>라이선스 표기(MIT·BSD-2-Clause·Apache-2.0 …)를 전문 절의 키(mit|bsd|apache)로 바꾼다.</summary>
    public static string? LicenseKey(string license) =>
        license.Contains("Apache", StringComparison.OrdinalIgnoreCase) ? "apache"
        : license.Contains("BSD", StringComparison.OrdinalIgnoreCase) ? "bsd"
        : license.Contains("MIT", StringComparison.OrdinalIgnoreCase) ? "mit"
        : null;

    /// <summary>전문 절("## MIT License" / "## BSD 2-Clause License" / "## Apache License 2.0")을 다음 절 직전까지 돌려준다.</summary>
    public static string? LicenseText(string text, string key)
    {
        var heading = key switch
        {
            "mit" => "## MIT License",
            "bsd" => "## BSD 2-Clause License",
            "apache" => "## Apache License",
            _ => null,
        };
        if (heading is null)
        {
            return null;
        }

        var start = text.IndexOf("\n" + heading, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start++;
        var next = text.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
        var body = next < 0 ? text[start..] : text[start..next];
        return body.Replace("```text\n", "").Replace("```", "").Trim();
    }
}
