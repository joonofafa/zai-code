using MoaiCode.Localization;

namespace MoaiCode.Cli;

/// <summary>
/// Windows 셸 통합(설치/제거): 사용자 PATH 등록 + 탐색기 우클릭 "Z.ai Code로 열기".
/// 전부 HKCU 범위라 관리자 권한이 필요 없다. Windows 11 은 새 우클릭 메뉴를 쓰므로 이 항목은
/// "추가 옵션 표시"(Shift+F10) 안의 클래식 메뉴에 나타난다(기본 메뉴 노출은 MSIX 패키징 필요).
/// Windows 외 플랫폼에서는 아무 것도 하지 않고 안내만 돌려준다.
/// </summary>
public static class WindowsIntegration
{
    // 레지스트리 키 이름(제거 시 동일 키를 지운다). 표시 문구와 분리해 언어를 바꿔도 제거가 되게 한다.
    private const string VerbKey = "MoaiCode";

    /// <summary>설치 위치 — %LOCALAPPDATA%\Programs\Z.ai Code. 다운로드 폴더를 지워도 깨지지 않게 복사한다.</summary>
    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "Z.ai Code");

    /// <summary>이미 셸 통합이 설치돼 있는지(우클릭 메뉴 키 존재 여부). 로그인 후 재차 묻지 않으려고 사용.</summary>
    public static bool IsInstalled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }
#if WINDOWS
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser
                .OpenSubKey($@"Software\Classes\Directory\shell\{VerbKey}");
            return key is not null;
        }
        catch
        {
            return false;
        }
#else
        return false;
#endif
    }

    /// <summary>PATH 등록 + 우클릭 메뉴 등록. 결과 메시지(사용자 표시용)를 돌려준다.</summary>
    public static string Install(string menuLabel)
    {
        if (!OperatingSystem.IsWindows())
        {
            return L10n.Get("cli.win.installWindowsOnly");
        }
#if WINDOWS
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                return L10n.Get("cli.win.installNoExePath");
            }

            var lines = new List<string>();
            var target = Path.Combine(InstallDir, "moai.exe");

            // 1) 실행 파일 복사 (이미 설치 위치에서 실행 중이면 건너뜀 — 자기 자신은 덮어쓸 수 없다).
            if (!string.Equals(Path.GetFullPath(exe), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(InstallDir);
                File.Copy(exe, target, overwrite: true);
                lines.Add(L10n.Get("cli.win.copied", target));
            }
            else
            {
                lines.Add(L10n.Get("cli.win.runningFromInstall", target));
            }

            // 2) 사용자 PATH 등록 (.NET 이 WM_SETTINGCHANGE 를 브로드캐스트하므로 새 터미널부터 적용).
            var path = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
            var already = path.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Any(p => string.Equals(p.Trim().TrimEnd('\\'), InstallDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            if (!already)
            {
                var updated = path.Length == 0 ? InstallDir : path.TrimEnd(';') + ";" + InstallDir;
                Environment.SetEnvironmentVariable("Path", updated, EnvironmentVariableTarget.User);
                lines.Add(L10n.Get("cli.win.pathAdded"));
            }
            else
            {
                lines.Add(L10n.Get("cli.win.pathAlready"));
            }

            // 3) 탐색기 우클릭 메뉴 — 폴더 자체와 폴더 빈 공간(배경) 양쪽에 등록.
            var command = BuildLaunchCommand(target);
            RegisterVerb(@"Directory\shell", menuLabel, target, command);
            RegisterVerb(@"Directory\Background\shell", menuLabel, target, command);
            lines.Add(L10n.Get("cli.win.verbAdded", menuLabel));
            lines.Add(L10n.Get("cli.win.win11Note"));

            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return L10n.Get("cli.win.installFailed", ex.Message);
        }
#else
        return L10n.Get("cli.win.installWindowsOnly");
#endif
    }

    /// <summary>설치한 PATH 항목과 우클릭 메뉴를 제거한다.</summary>
    public static string Uninstall()
    {
        if (!OperatingSystem.IsWindows())
        {
            return L10n.Get("cli.win.uninstallWindowsOnly");
        }
#if WINDOWS
        try
        {
            var lines = new List<string>();

            // 1) 우클릭 메뉴 제거.
            var removed = UnregisterVerb(@"Directory\shell") | UnregisterVerb(@"Directory\Background\shell");
            lines.Add(removed ? L10n.Get("cli.win.verbRemoved") : L10n.Get("cli.win.verbNone"));

            // 2) PATH 에서 설치 경로 제거.
            var path = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
            var parts = path.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
            var kept = parts.Where(p =>
                !string.Equals(p.Trim().TrimEnd('\\'), InstallDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)).ToList();
            if (kept.Count != parts.Count)
            {
                Environment.SetEnvironmentVariable("Path", string.Join(";", kept), EnvironmentVariableTarget.User);
                lines.Add(L10n.Get("cli.win.pathRemoved"));
            }
            else
            {
                lines.Add(L10n.Get("cli.win.pathNotFound"));
            }

            // 3) 설치 폴더 삭제 — 단, 지금 그 실행 파일로 돌고 있으면 지울 수 없다(윈도우 파일 잠금).
            var exe = Environment.ProcessPath ?? "";
            var runningFromInstall = exe.StartsWith(InstallDir, StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(InstallDir))
            {
                if (runningFromInstall)
                {
                    lines.Add(L10n.Get("cli.win.runningKept", InstallDir));
                }
                else
                {
                    Directory.Delete(InstallDir, recursive: true);
                    lines.Add(L10n.Get("cli.win.dirDeleted", InstallDir));
                }
            }

            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return L10n.Get("cli.win.uninstallFailed", ex.Message);
        }
#else
        return L10n.Get("cli.win.uninstallWindowsOnly");
#endif
    }

#if WINDOWS
    // 콘솔 TUI 라 터미널 창이 필요하다. Windows Terminal 이 있으면 그걸로(작업 디렉터리 -d),
    // 없으면 cmd 의 start 로 새 콘솔을 띄우며 /D 로 작업 디렉터리를 지정한다. %V = 선택/현재 폴더.
    private static string BuildLaunchCommand(string exePath)
    {
        var wt = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "wt.exe");
        return File.Exists(wt)
            ? $"\"{wt}\" -d \"%V\" \"{exePath}\""
            : $"cmd.exe /c start \"Z.ai Code\" /D \"%V\" \"{exePath}\"";
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RegisterVerb(string shellPath, string label, string iconPath, string command)
    {
        using var shell = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\{shellPath}\{VerbKey}");
        shell.SetValue(null, label);          // 메뉴에 보이는 문구
        shell.SetValue("Icon", iconPath);     // exe 아이콘 사용
        using var cmd = shell.CreateSubKey("command");
        cmd.SetValue(null, command);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool UnregisterVerb(string shellPath)
    {
        using var parent = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"Software\Classes\{shellPath}", writable: true);
        if (parent?.OpenSubKey(VerbKey) is null)
        {
            return false;
        }

        parent.DeleteSubKeyTree(VerbKey);
        return true;
    }
#endif
}
