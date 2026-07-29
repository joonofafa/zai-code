using System.Linq;
using DocumentFormat.OpenXml.CustomProperties;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.VariantTypes;

namespace MoaiCode.Tools.OpenXml;

/// <summary>
/// 문서에 우리(MoAI)가 심는 고유 식별자(custom document property "MoaiDocId").
/// 우리가 생성한 문서를 나중에(이름을 바꿔 저장했더라도) 같은 대화로 되찾기 위한 표식.
/// Save/Save-As 시 custom property 는 보존되므로 버전 난립에도 계보가 유지된다.
/// 기존(외부) 문서에는 이 property 가 없어 null 이 반환된다 — 그 경우는 경로/내용으로 매칭.
/// </summary>
public static class OfficeDocId
{
    public const string PropertyName = "MoaiDocId";

    // OOXML custom property 표준 FMTID.
    private const string FmtId = "{D5CDD505-2E9C-101B-9397-08002B2CF9AE}";

    /// <summary>새 MoaiDocId(GUID)를 담은 custom properties 파트 내용을 만든다.</summary>
    public static Properties Build(string id) => new(
        new CustomDocumentProperty(new VTLPWSTR(id))
        {
            FormatId = FmtId,
            PropertyId = 2, // 1 은 예약, 사용자 property 는 2 부터.
            Name = PropertyName,
        });

    /// <summary>새 GUID 문자열.</summary>
    public static string NewId() => System.Guid.NewGuid().ToString();

    /// <summary>파일에서 MoaiDocId 를 읽는다. 없거나 실패하면 null.</summary>
    public static string? Read(string path)
    {
        try
        {
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".docx" => From(WordprocessingDocument.Open(path, false)),
                ".xlsx" => From(SpreadsheetDocument.Open(path, false)),
                ".pptx" => From(PresentationDocument.Open(path, false)),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? From(OpenXmlPackage pkg)
    {
        using (pkg)
        {
            var part = pkg.GetPartsOfType<CustomFilePropertiesPart>().FirstOrDefault();
            var prop = part?.Properties?
                .Elements<CustomDocumentProperty>()
                .FirstOrDefault(p => p.Name?.Value == PropertyName);
            return prop?.VTLPWSTR?.Text;
        }
    }
}
