using System.Text.Json;
using MoaiCode.Core;

namespace MoaiCode.Persistence;

/// <summary>
/// 입력 히스토리 영속화 (JSONL, TS history.ts 대응). 한 줄당 한 항목.
/// 화살표 네비게이션(raw 입력)은 후속; 현재는 저장 + 최근 조회 + /history 표시.
/// </summary>
public sealed class HistoryStore
{
    private readonly string _path;

    public HistoryStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".moai", "history.jsonl");
    }

    public async Task AppendAsync(string entry, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(entry))
        {
            return;
        }

        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            FilePermissions.RestrictDirToUser(dir);
        }

        var existed = File.Exists(_path);
        var line = JsonSerializer.Serialize(new HistoryEntry(entry));
        await File.AppendAllTextAsync(_path, line + "\n", ct).ConfigureAwait(false);
        if (!existed)
        {
            FilePermissions.RestrictFileToUser(_path); // 새로 만든 경우 0600 (사용자 입력 포함)
        }
    }

    /// <summary>최근 n개를 오래된→최신 순으로 반환.</summary>
    public async Task<IReadOnlyList<string>> RecentAsync(int n, CancellationToken ct = default)
    {
        if (!File.Exists(_path))
        {
            return Array.Empty<string>();
        }

        var lines = await File.ReadAllLinesAsync(_path, ct).ConfigureAwait(false);
        var result = new List<string>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var e = JsonSerializer.Deserialize<HistoryEntry>(line);
                if (e is not null && !string.IsNullOrEmpty(e.Display))
                {
                    result.Add(e.Display);
                }
            }
            catch (JsonException)
            {
                // 손상된 줄 무시
            }
        }

        return result.Count <= n ? result : result.GetRange(result.Count - n, n);
    }

    private sealed record HistoryEntry(string Display);
}
