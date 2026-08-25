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
    private readonly int _retainCount;
    private readonly int _retainDays;
    private bool _swept;

    /// <param name="retainCount">최근 N개만 유지(0=무제한). <param name="retainDays">N일 초과 삭제(0=사용 안 함).
    /// 둘 다 저장 시 적용, 현재 세션은 항상 보존. 값은 Config 에서 주입(Persistence→Config 결합 회피).</param></param>
    public SessionStore(string? baseDir = null, int retainCount = 0, int retainDays = 0)
    {
        _baseDir = baseDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".zaicode", "sessions");
        _retainCount = retainCount;
        _retainDays = retainDays;
    }

    public string PathFor(string sessionId)
        => Path.Combine(_baseDir, $"{sessionId}.jsonl");

    public async Task SaveAsync(
        string sessionId, IReadOnlyList<Message> messages, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_baseDir);
        FilePermissions.RestrictDirToUser(_baseDir);   // 세션 디렉토리 0700
        SweepPermissionsOnce();                        // 옛 파일 0600 재적용(프로세스/인스턴스당 1회)
        var sb = new StringBuilder();
        foreach (var m in messages)
        {
            sb.AppendLine(JsonSerializer.Serialize<Message>(m, Json));
        }

        var path = PathFor(sessionId);
        await File.WriteAllTextAsync(path, sb.ToString(), ct).ConfigureAwait(false);
        // 트랜스크립트에 tool 출력·사용자 입력(시크릿 가능)이 담기므로 사용자 전용(0600).
        FilePermissions.RestrictFileToUser(path);

        EnforceRetention(sessionId);                   // 보존 정책: 상위 N개/N일 밖 정리(현재 세션 제외)
    }

    /// <summary>
    /// 권한 강제 코드 도입 전 저장된 세션 파일은 0664 로 남아 다시 저장될 일이 없어 영구히 그 상태다.
    /// 첫 저장 시 1회 전수 스윕해 0600 을 재적용한다(매 저장마다 stat 회피 — 인스턴스당 1회).
    /// </summary>
    private void SweepPermissionsOnce()
    {
        if (_swept)
        {
            return;
        }

        _swept = true;
        try
        {
            foreach (var path in Directory.EnumerateFiles(_baseDir, "*.jsonl"))
            {
                try
                {
                    FilePermissions.RestrictFileToUser(path);
                }
                catch
                {
                    // 개별 파일 실패는 무시 — 저장 경로를 막지 않는다(기존 코드 스타일).
                }
            }
        }
        catch
        {
            // 디렉토리 열거 실패도 non-fatal.
        }
    }

    /// <summary>
    /// 세션 파일이 영구 누적되지 않도록 저장 시 정리한다. retainCount 밖(최근순) 또는 retainDays 초과 파일을
    /// 삭제하되 현재 세션은 항상 보존. 둘 다 0(미설정)이면 정리하지 않는다. 실패는 조용히 무시(non-fatal).
    /// </summary>
    private void EnforceRetention(string currentSessionId)
    {
        if (_retainCount <= 0 && _retainDays <= 0)
        {
            return;
        }

        try
        {
            var files = Directory.GetFiles(_baseDir, "*.jsonl")
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            DateTime? cutoff = _retainDays > 0 ? DateTime.UtcNow.AddDays(-_retainDays) : null;

            for (var i = 0; i < files.Count; i++)
            {
                var f = files[i];
                if (string.Equals(Path.GetFileNameWithoutExtension(f.Name), currentSessionId, StringComparison.Ordinal))
                {
                    continue; // 현재 세션은 항상 보존
                }

                var overCount = _retainCount > 0 && i >= _retainCount;
                var tooOld = cutoff is { } c && f.LastWriteTimeUtc < c;
                if (overCount || tooOld)
                {
                    try
                    {
                        f.Delete();
                    }
                    catch
                    {
                        // 개별 삭제 실패는 무시.
                    }
                }
            }
        }
        catch
        {
            // 열거 실패는 non-fatal.
        }
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

    /// <summary>세션 파일 삭제. 없으면 no-op(성공 취급). I/O 실패 시 false.</summary>
    public bool Delete(string sessionId)
    {
        try
        {
            var path = PathFor(sessionId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
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
