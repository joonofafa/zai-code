namespace MoaiCode.Core;

/// <summary>
/// 민감 파일(자격증명·세션·히스토리·로그)을 소유자 전용 권한으로 제한. Unix 에서만 동작하고
/// Windows/실패는 조용히 무시(베스트 에포트). CredentialStore 의 0600 정책과 동형.
/// </summary>
public static class FilePermissions
{
    /// <summary>파일을 0600(소유자 읽기/쓰기)으로. 존재하지 않거나 Windows면 no-op.</summary>
    public static void RestrictFileToUser(string path)
    {
        if (OperatingSystem.IsWindows() || string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>디렉토리를 0700(소유자 전용)으로. 존재하지 않거나 Windows면 no-op.</summary>
    public static void RestrictDirToUser(string dir)
    {
        if (OperatingSystem.IsWindows() || string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
