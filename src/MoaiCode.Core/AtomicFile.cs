using System.Text;

namespace MoaiCode.Core;

/// <summary>
/// 원자적 파일 쓰기: 임시파일(생성 시점부터 0600)에 쓰고 디스크 flush 후 rename 으로 마무리한다.
/// 직접 truncate 쓰기는 저장 중 크래시/정전 시 기존 파일을 반쯤 깨진 상태로 남긴다.
/// Config.SecureFile 과 같은 패턴을 일반 데이터(세션·사용량·청크 캐시)에도 적용.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string? contents)
        => WriteAllBytes(path, Encoding.UTF8.GetBytes(contents ?? string.Empty));

    public static async Task WriteAllTextAsync(
        string path, string contents, CancellationToken ct = default)
    {
        var (fs, tmp) = CreateTmp(path);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(contents);
            await fs.WriteAsync(bytes, ct).ConfigureAwait(false);
            Finish(fs, tmp, path);
        }
        catch
        {
            Cleanup(fs, tmp);
            throw;
        }
    }

    public static void WriteAllBytes(string path, byte[] bytes)
    {
        var (fs, tmp) = CreateTmp(path);
        try
        {
            fs.Write(bytes, 0, bytes.Length);
            Finish(fs, tmp, path);
        }
        catch
        {
            Cleanup(fs, tmp);
            throw;
        }
    }

    // flush-to-disk(fsync) 후 원자적 교체. rename 은 퍼미션을 유지하므로 결과물은 0600.
    private static void Finish(FileStream fs, string tmp, string path)
    {
        fs.Flush(flushToDisk: true);
        fs.Dispose();
        File.Move(tmp, path, overwrite: true);
    }

    private static void Cleanup(FileStream fs, string tmp)
    {
        try { fs.Dispose(); } catch { }
        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
    }

    private static (FileStream Fs, string Tmp) CreateTmp(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // 유일한 tmp 이름 — 같은 파일에 대한 동시 저장이 서로 덮어쓰지 않게 한다.
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        if (OperatingSystem.IsWindows())
        {
            return (new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None), tmp);
        }

        var opts = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };
        return (new FileStream(tmp, opts), tmp);
    }
}
