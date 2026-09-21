using System.Linq;
using DocumentFormat.OpenXml.CustomProperties;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.VariantTypes;

namespace MoaiCode.Tools.OpenXml;

/// <summary>생성 문서에 심는 브랜드 테마(색 hex 6자리·폰트명). 편집 툴이 읽어 기본값으로 쓴다.</summary>
public sealed record DocTheme(string? Accent, string? Bg, string? Text, string? TitleFont, string? BodyFont);

/// <summary>
/// 문서가 자기 테마를 알게 한다 — 커스텀 문서 속성 MoaiTheme*(accent/bg/text/폰트)에 기록.
/// 생성(PptxCreate/DocxCreate/XlsxCreate) 시 스탬프하고, COM 편집 툴이 열린 문서에서 읽어
/// insert_table·add_designed_slide 등의 색·폰트 기본값으로 사용한다(모델의 색 유추 의존 제거).
/// <see cref="DocSessionRef"/> 와 같은 custom.xml 자리를 쓴다. 실패는 모두 non-fatal.
/// </summary>
public static class DocThemeStamp
{
    public const string AccentProperty = "MoaiThemeAccent";
    public const string BgProperty = "MoaiThemeBg";
    public const string TextProperty = "MoaiThemeText";
    public const string TitleFontProperty = "MoaiThemeTitleFont";
    public const string BodyFontProperty = "MoaiThemeBodyFont";
    private const string FmtId = "{D5CDD505-2E9C-101B-9397-08002B2CF9AE}";
    private static readonly string[] AllNames = { AccentProperty, BgProperty, TextProperty, TitleFontProperty, BodyFontProperty };

    /// <summary>테마를 파일에 기록/갱신. 빈 값 필드는 생략. 실패는 false.</summary>
    public static bool Stamp(string path, DocTheme theme)
    {
        try
        {
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".docx": using (var d = WordprocessingDocument.Open(path, true)) { StampCore(d, theme); } return true;
                case ".xlsx": using (var s = SpreadsheetDocument.Open(path, true)) { StampCore(s, theme); } return true;
                case ".pptx": using (var p = PresentationDocument.Open(path, true)) { StampCore(p, theme); } return true;
                default: return false;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>파일의 테마 스탬프. 없거나 실패면 null.</summary>
    public static DocTheme? Read(string path)
    {
        try
        {
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".docx" => ReadCore(WordprocessingDocument.Open(path, false)),
                ".xlsx" => ReadCore(SpreadsheetDocument.Open(path, false)),
                ".pptx" => ReadCore(PresentationDocument.Open(path, false)),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>"#RRGGBB"/"RRGGBB" → 대문자 6자리, 그 외 null (스탬프 값 정규화).</summary>
    public static string? NormalizeHex(string? v)
    {
        if (string.IsNullOrWhiteSpace(v))
        {
            return null;
        }

        var h = v.Trim().TrimStart('#').ToUpperInvariant();
        return h.Length == 6 && h.All(System.Uri.IsHexDigit) ? h : null;
    }

    private static void StampCore(OpenXmlPackage pkg, DocTheme t)
    {
        var part = pkg.GetPartsOfType<CustomFilePropertiesPart>().FirstOrDefault() ?? AddPart(pkg);
        part.Properties ??= new Properties();
        var props = part.Properties;
        foreach (var p in props.Elements<CustomDocumentProperty>().Where(p => AllNames.Contains(p.Name?.Value)).ToList())
        {
            p.Remove();
        }

        var nextId = props.Elements<CustomDocumentProperty>().Select(p => p.PropertyId?.Value ?? 1).DefaultIfEmpty(1).Max() + 1;
        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                props.AppendChild(new CustomDocumentProperty(new VTLPWSTR(value)) { FormatId = FmtId, PropertyId = nextId++, Name = name });
            }
        }

        Add(AccentProperty, NormalizeHex(t.Accent));
        Add(BgProperty, NormalizeHex(t.Bg));
        Add(TextProperty, NormalizeHex(t.Text));
        Add(TitleFontProperty, t.TitleFont?.Trim());
        Add(BodyFontProperty, t.BodyFont?.Trim());
        props.Save();
    }

    private static CustomFilePropertiesPart AddPart(OpenXmlPackage pkg) => pkg switch
    {
        WordprocessingDocument d => d.AddCustomFilePropertiesPart(),
        SpreadsheetDocument s => s.AddCustomFilePropertiesPart(),
        PresentationDocument p => p.AddCustomFilePropertiesPart(),
        _ => throw new System.NotSupportedException(),
    };

    private static DocTheme? ReadCore(OpenXmlPackage pkg)
    {
        using (pkg)
        {
            var props = pkg.GetPartsOfType<CustomFilePropertiesPart>().FirstOrDefault()?.Properties;
            if (props is null)
            {
                return null;
            }

            string? Val(string name) => props.Elements<CustomDocumentProperty>().FirstOrDefault(p => p.Name?.Value == name)?.VTLPWSTR?.Text;
            var t = new DocTheme(Val(AccentProperty), Val(BgProperty), Val(TextProperty), Val(TitleFontProperty), Val(BodyFontProperty));
            return t.Accent is null && t.Bg is null && t.Text is null && t.TitleFont is null && t.BodyFont is null ? null : t;
        }
    }
}
