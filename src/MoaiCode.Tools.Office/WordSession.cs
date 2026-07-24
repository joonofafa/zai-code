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
            paras.Add(new WordParaInfo(i, text.Trim('\r', '\n', '\a', ' '), style));
        }

        return new WordDocInfo(
            Name: (string)doc.Name,
            Path: TryGet(() => (string?)doc.FullName, null),
            ParagraphCount: paraCount,
            SelectionText: selection,
            Paragraphs: paras);
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
