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

        // TODO(Phase 1): PowerPoint/Excel Inspect/Edit 툴 등록.
        //   COM 연결 실패(Office 미설치/GPO 차단)는 여기서 잡아 빈 목록 반환.
        return Array.Empty<ITool>();
    }
}
