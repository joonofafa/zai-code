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
            ".zaicode", "credentials.json");
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
        // Race-free write: 임시파일(생성 순간 0600)에 쓰고 원자적 이동. 공용 헬퍼 사용(SecureFile).
        SecureFile.Write(_path, JsonSerializer.SerializeToUtf8Bytes(dict), lockDownDir: true);
    }
}
