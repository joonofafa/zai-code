using System;
using System.Collections.Generic;
using MoaiCode.Config;

namespace MoaiCode.Tools.Office;

/// <summary>열린 Office 문서 하나(앱·이름·ProgId·전체경로).
/// Path 는 COM 의 FullName(전체 경로) — 저장 안 된 새 문서는 이름만 올 수 있고, 실패 시 null.
/// MoaiDocId 조회·경로 기반 대화 재연결에 쓰인다.</summary>
public sealed record OfficeDoc(string App, string Name, string ProgId, string? Path = null)
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
                    list.Add(new OfficeDoc(appName, (string)item.Name, progId, TryFullName(item)));
                }
                catch (Exception ex)
                {
                    MoaiLog.Debug($"OfficeList: skip one {appName} item: {ex.GetType().Name}");
                }
            }
        }
        catch (Exception ex)
        {
            MoaiLog.Debug($"OfficeList: {appName} not available: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // COM 문서의 FullName(전체 경로). 저장 안 된 새 문서는 이름만 오거나 실패 → null.
    private static string? TryFullName(dynamic item)
    {
        try
        {
            var full = (string?)item.FullName;
            // 저장 전 문서는 FullName 이 Name 과 같음(경로 구분자 없음) → 경로로 취급하지 않는다.
            return !string.IsNullOrEmpty(full) && (full.Contains('\\') || full.Contains('/')) ? full : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Office 앱을 새로 실행한다("PowerPoint"|"Word"|"Excel"). Windows 전용.</summary>
    public static bool Launch(string app)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var exe = app switch
        {
            "PowerPoint" => "powerpnt",
            "Word" => "winword",
            "Excel" => "excel",
            _ => null,
        };
        if (exe is null)
        {
            return false;
        }

        try
        {
            // App Paths 레지스트리로 실행 파일 해석 → ShellExecute 필요.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
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
                MoaiLog.Warn($"OfficeActivate: cannot reach {doc.App} via COM (app is null)");
                return false;
            }

            switch (doc.App)
            {
                case "PowerPoint":
                    foreach (var p in app.Presentations)
                    {
                        if ((string)p.Name == doc.Name)
                        {
                            p.Windows.Item(1).Activate();
                            BringAppToFront(app);
                            return true;
                        }
                    }

                    break;
                case "Excel":
                    foreach (var w in app.Workbooks)
                    {
                        if ((string)w.Name == doc.Name)
                        {
                            w.Activate();
                            BringAppToFront(app);
                            return true;
                        }
                    }

                    break;
                case "Word":
                    foreach (var d in app.Documents)
                    {
                        if ((string)d.Name == doc.Name)
                        {
                            d.Activate();
                            BringAppToFront(app);
                            return true;
                        }
                    }

                    break;
            }

            MoaiLog.Warn($"OfficeActivate: document '{doc.Name}' not found among open {doc.App} docs");
        }
        catch (Exception ex)
        {
            MoaiLog.Error($"OfficeActivate: {doc.App} '{doc.Name}' threw", ex);
            return false;
        }

        return false;
    }

    // 오피스 앱 창을 화면 앞으로. 문서 Activate 만으로는 앱 창이 뒤에 남을 수 있어 앱 자체도 활성화한다.
    private static void BringAppToFront(dynamic app)
    {
        try
        {
            app.Visible = true; // 최소화/숨김 상태 복원
        }
        catch
        {
            // Visible 미지원/차단 — 무시.
        }

        try
        {
            app.Activate(); // 앱 창을 포그라운드로
        }
        catch
        {
            // Activate 실패 — 무시.
        }
    }
}
