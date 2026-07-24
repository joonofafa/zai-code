using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MoaiCode.Config;

namespace MoaiCode.Tools.Office;

/// <summary>
/// 실행 중인 COM 서버(PowerPoint/Excel)를 Running Object Table 에서 가져온다.
/// modern .NET 의 BCL 에는 Marshal.GetActiveObject 가 없어 oleaut32 를 직접 P/Invoke 한다.
/// (DllImport 는 메타데이터라 Linux 에서 컴파일되며, 실제 호출은 Windows 에서만 일어난다.)
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ComInterop
{
    /// <summary>실행 중인 인스턴스가 있으면 반환, 없으면 null. 새 인스턴스를 만들지 않는다.</summary>
    public static object? TryGetActiveObject(string progId)
    {
        try
        {
            var type = Type.GetTypeFromProgID(progId);
            if (type is null)
            {
                MoaiLog.Warn($"COM: ProgID '{progId}' not registered (Office not installed?)");
                return null;
            }

            var clsid = type.GUID;
            var hr = GetActiveObject(ref clsid, IntPtr.Zero, out var obj);
            if (hr != 0)
            {
                // 0x800401E3 (MK_E_UNAVAILABLE): running instance not in ROT for THIS
                // desktop/integrity level -> usually a UAC elevation mismatch between this
                // app and the Office process, or the doc is not registered in the ROT.
                MoaiLog.Warn(
                    $"COM: GetActiveObject('{progId}') hr=0x{(uint)hr:X8}" +
                    (hr == unchecked((int)0x800401E3)
                        ? " (MK_E_UNAVAILABLE: no running instance visible; check UAC/elevation mismatch)"
                        : string.Empty));
                return null;
            }

            MoaiLog.Debug($"COM: GetActiveObject('{progId}') ok, thread STA={System.Threading.Thread.CurrentThread.GetApartmentState()}");
            return obj;
        }
        catch (Exception ex)
        {
            MoaiLog.Error($"COM: GetActiveObject('{progId}') threw", ex);
            return null;
        }
    }

    [DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern int GetActiveObject(
        ref Guid rclsid, IntPtr pvReserved, [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);
}
