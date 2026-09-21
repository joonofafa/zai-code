using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;

namespace MoaiCode.Tools.OpenXml;

/// <summary>
/// 템플릿에서 "브랜디드 빈 덱"을 만든다 — 슬라이드 마스터·레이아웃·테마(브랜드)는 유지하고 본문
/// 슬라이드(예시 내용)만 전부 제거. 새 문서를 이 마스터 위에서 시작해 내용을 생성하기 위한 베이스.
/// OpenXML 오프라인 처리(PowerPoint 불필요).
/// </summary>
public static class TemplateBlankBuilder
{
    /// <summary>src 를 dest 로 복사한 뒤 본문 슬라이드를 모두 제거한다. 성공 시 true.</summary>
    public static bool CreateBrandedBlank(string srcPath, string destPath)
    {
        try
        {
            File.Copy(srcPath, destPath, overwrite: true);
            StripSlides(destPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void StripSlides(string path)
    {
        using var doc = PresentationDocument.Open(path, true);
        var pp = doc.PresentationPart;
        var idList = pp?.Presentation?.SlideIdList;
        if (pp is null || idList is null)
        {
            return; // 슬라이드 목록 없음 — 그대로(마스터/레이아웃만 남음)
        }

        foreach (var sid in idList.Elements<SlideId>().ToList())
        {
            // 파트를 먼저 잡고(id 로 조회), id 를 목록에서 제거한 뒤 파트를 삭제한다.
            var rid = sid.RelationshipId?.Value;
            SlidePart? sp = rid is not null && pp.GetPartById(rid) is SlidePart p ? p : null;
            sid.Remove();
            if (sp is not null)
            {
                pp.DeletePart(sp);
            }
        }

        pp.Presentation.Save();
    }
}
