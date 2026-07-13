using MoaiCode.Core.Tools;

namespace MoaiCode.Tools.Office;

/// <summary>
/// Windows 에서 사용 가능한 Office COM 툴(PowerPoint/Excel)을 조립한다.
/// Office 미설치·COM 자동화 차단(고객사 GPO 등) 시 빈 목록을 반환해 조용히 비활성화한다.
/// 현재는 골격 — 실제 툴은 Phase 1(트랙 L 골격) 이후, COM 동작은 Phase 0 스파이크(Windows) 이후.
/// </summary>
public static class OfficeTools
{
    public static IReadOnlyList<ITool> CreateIfAvailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<ITool>();
        }

        // COM 은 전용 STA 스레드에서 직렬화한다. 디스패처는 세션 수명 동안 유지한다.
        // 실제 COM 연결은 툴 실행 시점에 시도하고, 실패(Office 미설치/GPO 차단)는 툴이 처리한다.
        return BuildTools(new StaDispatcher());
    }

    /// <summary>
    /// 주어진 디스패처로 Office 툴 목록을 만든다(OS 가드 없음). 툴 생성 자체는 COM 을 건드리지
    /// 않으므로 계약 테스트에서 플랫폼과 무관하게 호출할 수 있다. 실제 COM 은 ExecuteAsync 에서만.
    /// </summary>
    public static IReadOnlyList<ITool> BuildTools(StaDispatcher sta) => new ITool[]
    {
        new PowerPointInspectTool(sta),
        new PowerPointEditTool(sta),
        new ExcelInspectTool(sta),
        // TODO: PowerPoint Image/Slide/Save, Excel Edit/Formula/Chart/Sheet/Save (설계 §툴 설계)
    };

    public static IReadOnlyList<ITool> CreateIfAvailableForTests(StaDispatcher sta)
    {
        return new ITool[]
        {
            new PowerPointInspectTool(sta),
            new PowerPointEditTool(sta),
            new ExcelInspectTool(sta)
        };
    }
}
