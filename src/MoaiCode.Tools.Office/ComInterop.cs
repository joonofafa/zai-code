using System.Runtime.InteropServices;
using System.Runtime.Versioning;

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
                return null;
            }

            var clsid = type.GUID;
            var hr = GetActiveObject(ref clsid, IntPtr.Zero, out var obj);
            return hr == 0 ? obj : null;
        }
        catch (COMException)
        {
            return null; // MK_E_UNAVAILABLE 등 — 실행 중인 인스턴스 없음
        }
    }

    [DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern int GetActiveObject(
        ref Guid rclsid, IntPtr pvReserved, [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);
}
