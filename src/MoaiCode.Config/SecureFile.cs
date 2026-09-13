namespace MoaiCode.Config;

/// <summary>
/// 민감 파일 보안 쓰기 헬퍼. 임시파일(생성 순간 0600)에 쓰고 원자적 이동(rename)으로 마무리한다.
/// (File.WriteAllText 후 chmod 하면 짧게나마 world-readable 창이 열리고, 직접 truncate 쓰기는
/// 저장 중 크래시 시 파일을 망가뜨린다.)
/// </summary>
internal static class SecureFile
{
    /// <param name="lockDownDir">true면 부모 디렉토리도 0700으로(자격증명 저장소용).</param>
    public static void Write(string path, byte[] bytes, bool lockDownDir = false)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            if (lockDownDir && !OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(dir,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        var tmp = path + ".tmp";
        try
        {
            using (var fs = OpenTmp(tmp))
            {
                fs.Write(bytes, 0, bytes.Length);
            }

            // 원자적 교체 (동일 파일시스템). rename 은 퍼미션을 유지하므로 재적용 불필요.
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    // 임시파일 생성과 동시에 0600 적용 — world-readable window 자체가 없다.
    private static FileStream OpenTmp(string tmp)
    {
        if (OperatingSystem.IsWindows())
        {
            return new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
        }

        var opts = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };
        return new FileStream(tmp, opts);
    }
}
