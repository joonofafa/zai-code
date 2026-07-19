using System.Text;

namespace MoaiCode.Cli;

/// <summary>
/// Windows 콘솔 인코딩 초기화. 기본 출력 인코딩이 OEM 코드페이지(한국어 Windows=CP949)라
/// ❯·✓·• 같은 유니코드 기호가 '?'로 깨진다 → UTF-8 로 강제한다. 리다이렉트/콘솔없음은 건너뜀.
/// </summary>
internal static class ConsoleSetup
{
    public static void EnsureUtf8()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Unix 계열은 기본 UTF-8
        }

        try
        {
            if (!Console.IsOutputRedirected)
            {
                Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            }
        }
        catch (Exception)
        {
            // 콘솔 없음/리다이렉트 등 — 무시(치명적 아님).
        }
    }
}
