using System.Text;
using System.Text.Json;
using MoaiCode.Core;
using MoaiCode.Core.Messages;

namespace MoaiCode.Persistence;

/// <summary>
/// 세션 저장/복원 (JSONL, 한 줄당 한 메시지). 다형성 직렬화는 Message의
/// [JsonPolymorphic] 속성 기반. Phase 5에서 압축/마이그레이션/히스토리 확장.
/// </summary>
public sealed class SessionStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
    };

    private readonly string _baseDir;

    public SessionStore(string? baseDir = null)
    {
        _baseDir = baseDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".moai", "sessions");
    }

    public string PathFor(string sessionId)
        => Path.Combine(_baseDir, $"{sessionId}.jsonl");

    public async Task SaveAsync(
        string sessionId, IReadOnlyList<Message> messages, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_baseDir);
        FilePermissions.RestrictDirToUser(_baseDir);   // 세션 디렉토리 0700
        var sb = new StringBuilder();
        foreach (var m in messages)
        {
            sb.AppendLine(JsonSerializer.Serialize<Message>(m, Json));
        }

        var path = PathFor(sessionId);
        await File.WriteAllTextAsync(path, sb.ToString(), ct).ConfigureAwait(false);
        // 트랜스크립트에 tool 출력·사용자 입력(시크릿 가능)이 담기므로 사용자 전용(0600).
        FilePermissions.RestrictFileToUser(path);
    }

    public async Task<IReadOnlyList<Message>> LoadAsync(
        string sessionId, CancellationToken ct = default)
    {
        var path = PathFor(sessionId);
        if (!File.Exists(path))
        {
            return Array.Empty<Message>();
        }

        var lines = await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false);
        var result = new List<Message>(lines.Length);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var msg = JsonSerializer.Deserialize<Message>(line, Json);
            if (msg is not null)
            {
                result.Add(msg);
            }
        }

        return result;
    }

    public IReadOnlyList<string> ListSessions()
    {
        if (!Directory.Exists(_baseDir))
        {
            return Array.Empty<string>();
        }

        return Directory.GetFiles(_baseDir, "*.jsonl")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList();
    }

    /// <summary>세션 목록 + 메타데이터(수정시각·메시지수·첫 질문 제목), 최근 순.</summary>
    public async Task<IReadOnlyList<SessionInfo>> ListInfosAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_baseDir))
        {
            return Array.Empty<SessionInfo>();
        }

        var infos = new List<SessionInfo>();
        foreach (var path in Directory.GetFiles(_baseDir, "*.jsonl"))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            var modified = File.GetLastWriteTime(path);
            var count = 0;
            var title = "";
            try
            {
                var lines = await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false);
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    count++;
                    if (title.Length == 0)
                    {
                        Message? msg;
                        try
                        {
                            msg = JsonSerializer.Deserialize<Message>(line, Json);
                        }
                        catch (JsonException)
                        {
                            continue;
                        }

                        if (msg is UserMessage u
                            && !u.Text.StartsWith("<system-reminder>", StringComparison.Ordinal)
                            && !u.Text.StartsWith("[Summary of earlier conversation]", StringComparison.Ordinal))
                        {
                            title = u.Text.ReplaceLineEndings(" ").Trim();
                        }
                    }
                }
            }
            catch (IOException)
            {
                // skip unreadable
            }

            infos.Add(new SessionInfo(id, modified, count, title));
        }

        return infos.OrderByDescending(i => i.ModifiedAt).ToList();
    }
}

/// <summary>세션 목록 표시용 메타데이터.</summary>
public sealed record SessionInfo(string Id, DateTime ModifiedAt, int MessageCount, string Title);
