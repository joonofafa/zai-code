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

    /// <summary>세션 목록 + 메타데이터(수정시각·메시지수·제목), 최근 순 최대 99개.
    /// 제목은 첫 질문, 없으면 마지막 사용자 프롬프트로 대체.</summary>
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
            var lastPrompt = "";
            var originalRequest = "";
            string? lastSummary = null;
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
                    Message? msg;
                    try
                    {
                        msg = JsonSerializer.Deserialize<Message>(line, Json);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    if (msg is not UserMessage u)
                    {
                        continue;
                    }

                    // 압축 세션 폴백: 리마인더/요약에 박힌 'Original request:' 앵커를 잡아둔다.
                    var anchor = OriginalRequestAnchor(u.Text);
                    if (anchor.Length > 0)
                    {
                        originalRequest = anchor;
                    }

                    // 요약(컨텍스트 압축)은 별도 보관 — 일반 프롬프트가 하나도 없을 때 폴백으로만 쓴다.
                    if (u.Text.StartsWith("[Summary of earlier conversation]", StringComparison.Ordinal))
                    {
                        lastSummary = u.Text;
                        continue;
                    }

                    if (u.Text.StartsWith("<system-reminder>", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var t = u.Text.ReplaceLineEndings(" ").Trim();
                    if (t.Length > 0)
                    {
                        lastPrompt = t;
                    }
                }
            }
            catch (IOException)
            {
                // skip unreadable
            }

            // 제목 우선순위: 마지막 프롬프트 → Original request 앵커 → 요약 인용문.
            // (모두 없으면 표시 계층에서 일반 라벨로 대체 — '(제목 없음)' 노출 안 함.)
            var title = lastPrompt.Length > 0 ? lastPrompt
                : originalRequest.Length > 0 ? originalRequest
                : lastSummary is not null ? ExtractRequestFromSummary(lastSummary) : "";
            infos.Add(new SessionInfo(id, modified, count, title));
        }

        // 최근 순, 최대 99개까지만 노출(2자리 번호로 표기).
        return infos.OrderByDescending(i => i.ModifiedAt).Take(99).ToList();
    }

    // 압축 세션의 리마인더/요약에 박힌 'Original request: <원문>' 한 줄에서 원 요청을 뽑는다.
    private static string OriginalRequestAnchor(string text)
    {
        const string marker = "Original request:";
        var idx = text.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
        {
            return "";
        }

        var rest = text[(idx + marker.Length)..];
        var nl = rest.IndexOf('\n');
        if (nl >= 0)
        {
            rest = rest[..nl];
        }

        return rest.Trim().Trim('`').Trim();
    }

    // 컨텍스트 요약문에서 원 사용자 요청을 추출한다. 요약은 원 요청을 첫 인용문(> `...`)으로 담는다.
    private static string ExtractRequestFromSummary(string summary)
    {
        foreach (var raw in summary.Split('\n'))
        {
            var line = raw.TrimStart();
            if (line.Length == 0 || line[0] != '>')
            {
                continue;
            }

            var quote = line[1..].Trim().Trim('`').Trim();
            if (quote.Length > 0)
            {
                return quote.ReplaceLineEndings(" ");
            }
        }

        return "";
    }
}

/// <summary>세션 목록 표시용 메타데이터.</summary>
public sealed record SessionInfo(string Id, DateTime ModifiedAt, int MessageCount, string Title);
