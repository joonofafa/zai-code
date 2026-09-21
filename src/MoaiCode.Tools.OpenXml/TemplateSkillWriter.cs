using System.Text;

namespace MoaiCode.Tools.OpenXml;

/// <summary>
/// 템플릿 분석 결과를 SKILL.md(텍스트) 로 직렬화한다. 브랜드 테마(색·폰트)를 데이터로 싣는다
/// (Base64 임베드 금지 — 스킬은 컨텍스트에 주입됨). 사용 시점에 이 텍스트를 주입하면, 새 문서를
/// 오프라인 생성 툴(PptxCreate / DocxCreate / XlsxCreate)로 이 테마 값에 맞춰 만든다.
/// </summary>
public static class TemplateSkillWriter
{
    /// <summary>SKILL.md 본문. app = "PowerPoint" | "Word" | "Excel".</summary>
    public static string Build(string templateName, string app, TemplateAnalysis a)
    {
        var t = a.Theme;
        var sb = new StringBuilder();
        sb.AppendLine($"# Template brand style: {templateName}");
        sb.AppendLine();
        sb.AppendLine("Create a BRAND-NEW document whose content is generated from scratch, styled to match this");
        sb.AppendLine("template's brand theme (colors and fonts). Do NOT copy or fill the template's example");
        sb.AppendLine("content — generate fresh content and apply the theme values below.");
        sb.AppendLine();
        sb.AppendLine("## Brand theme values");
        Line(sb, "Accent color", t.AccentHex);
        Line(sb, "Background color", t.BgHex);
        Line(sb, "Text color", t.TextHex);
        Line(sb, "Title font", t.TitleFontEa ?? t.TitleFontLatin);
        Line(sb, "Body font", t.BodyFontEa ?? t.BodyFontLatin);
        sb.AppendLine();
        sb.AppendLine("## How to produce the document");

        switch (app)
        {
            case "PowerPoint":
                sb.AppendLine("Use PptxCreate ONCE with a workspace path and a slides[] array. Plan an outline and");
                sb.AppendLine("pick a fitting layout per slide (cover / section / content / two_col / stat / cards /");
                sb.AppendLine("process / table / quote), varying them. Pass the brand values as PptxCreate params:");
                sb.AppendLine($"  accent={t.AccentHex}, bg={t.BgHex}, text_color={t.TextHex},");
                sb.AppendLine($"  title_font={t.TitleFontEa ?? t.TitleFontLatin}, body_font={t.BodyFontEa ?? t.BodyFontLatin}");
                break;
            case "Word":
                sb.AppendLine("Use DocxCreate with blocks[] (headings, paragraphs, bullets, tables). Plan the sections");
                sb.AppendLine("for the topic. Pass the brand values as DocxCreate params so headings and text match:");
                sb.AppendLine($"  accent={t.AccentHex}, title_font={t.TitleFontEa ?? t.TitleFontLatin}, body_font={t.BodyFontEa ?? t.BodyFontLatin}");
                break;
            case "Excel":
                sb.AppendLine("Use XlsxCreate with sheets[] (rows). Plan the table(s) for the topic. Pass the brand");
                sb.AppendLine($"font so all cells match: body_font={t.BodyFontEa ?? t.BodyFontLatin}");
                break;
        }

        sb.AppendLine("Generate only what the content needs. The user opens the file with the Edit button afterwards.");
        return sb.ToString();
    }

    private static void Line(StringBuilder sb, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            sb.AppendLine($"- {label}: {value}");
        }
    }
}
