using System.Text.Json;

namespace MoaiCode.Config;

/// <summary>
/// 자격증명 저장 추상화 (TS의 secure storage 대응). 현재 파일 기반 구현 제공.
/// 추후 OS 키체인(Windows Credential Manager / macOS Keychain / libsecret) 구현으로 교체 가능.
/// </summary>
public interface ICredentialStore
{
    string? Get(string key);
    void Set(string key, string value);
    IReadOnlyList<string> Keys();
}

/// <summary>~/.moai/credentials.json 기반 저장소. unix에서는 0600 권한.</summary>
public sealed class FileCredentialStore : ICredentialStore
{
    private readonly string _path;

    public FileCredentialStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".moai", "credentials.json");
    }

    public string? Get(string key)
        => Load().TryGetValue(key, out var v) ? v : null;

    public void Set(string key, string value)
    {
        var dict = Load();
        dict[key] = value;
        Save(dict);
    }

    public IReadOnlyList<string> Keys() => Load().Keys.ToList();

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void Save(Dictionary<string, string> dict)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    // 부모 디렉토리도 사용자 전용(0700).
                    File.SetUnixFileMode(dir,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        var json = JsonSerializer.Serialize(dict);

        // Race-free write: 임시파일에 쓰고 chmod 후 원자적 이동.
        // (File.WriteAllText 이후 chmod 하면 짧게나마 world-readable 창이 열림.)
        var tmp = _path + ".tmp";
        try
        {
            // 임시파일 생성 즉시 0600 적용 (world-readable window 회피).
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows())
                {
                    try
                    {
                        File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                fs.Write(bytes, 0, bytes.Length);
            }

            // 원자적 교체 (동일 파일시스템). File.Move 는 퍼미션을 유지하므로 재적용 불필요.
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }
}
