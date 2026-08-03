using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using MoaiCode.Config;

namespace MoaiCode.Tools.Office;

/// <summary>
/// 실행 중인 Word 에 late-binding 으로 연결해 활성 문서·문단·선택을 조회한다.
/// 모든 COM 접근은 StaDispatcher 안에서 수행하고, 밖으로는 DTO 만 반환한다.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WordSession
{
    private readonly StaDispatcher _sta;

    public WordSession(StaDispatcher sta) => _sta = sta;

    public Task<WordDocInfo?> GetActiveDocumentAsync(int maxParagraphs = 200) =>
        _sta.InvokeAsync(() => Inspect(maxParagraphs));

    private static WordDocInfo? Inspect(int maxParagraphs)
    {
        MoaiLog.Debug($"WordInspect: enter on thread apartment={System.Threading.Thread.CurrentThread.GetApartmentState()}");

        dynamic? app = ComInterop.TryGetActiveObject("Word.Application");
        if (app is null)
        {
            MoaiLog.Warn("WordInspect: no running Word instance (app is null)");
            return null;
        }

        dynamic? doc = TryGet(() => app.ActiveDocument, "app.ActiveDocument");
        if (doc is null)
        {
            MoaiLog.Warn("WordInspect: Word running but ActiveDocument is null");
            return null;
        }

        string? selection = TryGet(() => (string?)app.Selection.Text, "app.Selection.Text");
        if (!string.IsNullOrEmpty(selection))
        {
            selection = selection.Trim();
        }

        var paras = new List<WordParaInfo>();
        int paraCount = (int)doc.Paragraphs.Count;
        for (var i = 1; i <= paraCount && paras.Count < maxParagraphs; i++)
        {
            dynamic para = doc.Paragraphs[i];
            var text = TryGet(() => (string?)para.Range.Text, null) ?? string.Empty;
            var style = TryGet(() => (string?)para.Style.NameLocal, null);

            // 문단 서식(모델이 문서 톤을 보고 판단하도록). mixed/undefined 값은 null 로.
            var size = TryGet(() => (double?)(float)para.Range.Font.Size, null);
            if (size is <= 0 or > 1638) // wdUndefined(9999999) 등 비정상 값 제외
            {
                size = null;
            }

            var fontName = TryGet(() => (string?)para.Range.Font.Name, null);
            if (string.IsNullOrWhiteSpace(fontName))
            {
                fontName = null;
            }

            var boldVal = TryGet(() => (int?)para.Range.Font.Bold, null);
            bool? bold = boldVal is -1 ? true : boldVal is 0 ? false : null; // 그 외(mixed)는 null

            var colorVal = TryGet(() => (int?)para.Range.Font.Color, null);
            var color = ColorHex(colorVal);

            paras.Add(new WordParaInfo(i, text.Trim('\r', '\n', '\a', ' '), style, size, fontName, bold, color));
        }

        return new WordDocInfo(
            Name: (string)doc.Name,
            Path: TryGet(() => (string?)doc.FullName, null),
            ParagraphCount: paraCount,
            SelectionText: selection,
            Paragraphs: paras,
            Shapes: ReadShapes(doc));
    }

    // 떠 있는 도형(doc.Shapes) 목록. 인라인 이미지(InlineShapes)는 회전/이동이 안 되므로 제외.
    private static IReadOnlyList<OfficeShapeInfo> ReadShapes(dynamic doc)
    {
        var shapes = new List<OfficeShapeInfo>();
        int count = TryGet(() => (int?)doc.Shapes.Count, null) ?? 0;
        for (var i = 1; i <= count; i++)
        {
            dynamic? shape = TryGet(() => doc.Shapes[i], null);
            if (shape is null)
            {
                continue;
            }

            shapes.Add(new OfficeShapeInfo(
                Index: i,
                Name: TryGet(() => (string?)shape.Name, null) ?? $"Shape{i}",
                Left: TryGet(() => (double?)Convert.ToDouble(shape.Left), null) ?? 0,
                Top: TryGet(() => (double?)Convert.ToDouble(shape.Top), null) ?? 0,
                Width: TryGet(() => (double?)Convert.ToDouble(shape.Width), null) ?? 0,
                Height: TryGet(() => (double?)Convert.ToDouble(shape.Height), null) ?? 0,
                Rotation: TryGet(() => (double?)Convert.ToDouble(shape.Rotation), null) ?? 0));
        }

        return shapes;
    }

    // Office COM 색(BGR int) → "#RRGGBB". wdColorAutomatic(-16777216) 등 특수값은 null.
    private static string? ColorHex(int? bgr)
    {
        if (bgr is null || bgr.Value < 0)
        {
            return null;
        }

        var c = bgr.Value;
        var r = c & 0xFF;
        var g = (c >> 8) & 0xFF;
        var b = (c >> 16) & 0xFF;
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private static T? TryGet<T>(Func<T> get, string? what)
    {
        try
        {
            return get();
        }
        catch (Exception ex)
        {
            if (what is not null)
            {
                MoaiLog.Debug($"WordInspect: COM access '{what}' failed: {ex.GetType().Name}: {ex.Message}");
            }

            return default;
        }
    }
}
