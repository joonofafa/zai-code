using System;
using System.Collections.Generic;

namespace MoaiCode.Tools.Office;

/// <summary>열린 Office 문서 하나(앱·이름·ProgId).</summary>
public sealed record OfficeDoc(string App, string Name, string ProgId)
{
    public string Display => $"{App} · {Name}";
}

/// <summary>
/// 현재 열려있는 Office 문서(PowerPoint/Excel/Word)를 COM 으로 열거·활성화한다.
/// Windows·Office 미설치 시 빈 목록. 선택 시 Activate() 로 해당 문서를 활성화하면
/// 편집 툴(ActivePresentation 등)이 그 문서를 대상으로 동작한다.
/// </summary>
public static class OfficeWindowLister
{
    public static IReadOnlyList<OfficeDoc> ListOpenDocuments()
    {
        var list = new List<OfficeDoc>();
        if (!OperatingSystem.IsWindows())
        {
            return list;
        }

        Collect(list, "PowerPoint.Application", "PowerPoint", app => app.Presentations);
        Collect(list, "Excel.Application", "Excel", app => app.Workbooks);
        Collect(list, "Word.Application", "Word", app => app.Documents);
        return list;
    }

    private static void Collect(List<OfficeDoc> list, string progId, string appName, Func<dynamic, dynamic> getCollection)
    {
        try
        {
            dynamic? app = ComInterop.TryGetActiveObject(progId);
            if (app is null)
            {
                return;
            }

            foreach (var item in getCollection(app))
            {
                try
                {
                    list.Add(new OfficeDoc(appName, (string)item.Name, progId));
                }
                catch
                {
                    // 개별 항목 접근 실패는 건너뜀.
                }
            }
        }
        catch
        {
            // 해당 앱 미실행/COM 차단 — 건너뜀.
        }
    }

    /// <summary>선택 문서를 활성화(편집 툴이 이 문서를 대상 삼도록). 성공 여부 반환.</summary>
    public static bool Activate(OfficeDoc doc)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            dynamic? app = ComInterop.TryGetActiveObject(doc.ProgId);
            if (app is null)
            {
                return false;
            }

            switch (doc.App)
            {
                case "PowerPoint":
                    foreach (var p in app.Presentations)
                    {
                        if ((string)p.Name == doc.Name) { p.Windows.Item(1).Activate(); return true; }
                    }

                    break;
                case "Excel":
                    foreach (var w in app.Workbooks)
                    {
                        if ((string)w.Name == doc.Name) { w.Activate(); return true; }
                    }

                    break;
                case "Word":
                    foreach (var d in app.Documents)
                    {
                        if ((string)d.Name == doc.Name) { d.Activate(); return true; }
                    }

                    break;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }
}
