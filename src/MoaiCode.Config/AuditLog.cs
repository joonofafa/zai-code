using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MoaiCode.Core;

namespace MoaiCode.Config;

/// <summary>
/// 감사 로그 — 위험 명령의 승인·거부·차단과, --ignore-confirm(배치 자동화)으로 확인 없이 실행한 모든 동작.
/// 형식은 오피스챗(moai-code-int, AuditLog 5287033)과 맞췄다: <c>~/.mate/logs/audit.jsonl</c>, 한 줄에 한 건(UTF-8, BOM 없음),
/// 공통 필드 ts·tool·action·app·target·hash·outcome. 셸 명령을 다루므로 누가(user·host·session·pid)·어떻게 승인했는지(approval)·
/// 사유(reason)·모드(mode)·작업 폴더(cwd)를 더 남긴다. 동작 로그(mate.log)와는 다른 파일이다.
/// <para>
/// 대상(target)은 평문이되 비밀번호·토큰·카드번호·주민번호는 가린다. hash 는 가린 target 의 SHA-256 앞 12자리(같은 대상끼리 대조용).
/// 파일은 Unix 에서 0600. 보관은 <see cref="Trim"/>(기본 90일, 0=영구).
/// </para>
/// <para>
/// 쓰기 실패: 사람이 승인한 기록은 실패해도 진행한다(오피스챗과 같음). 확인 없이 자동 승인하는 경우(--ignore-confirm)는
/// 기록이 승인의 조건이라, 기록하지 못하면 거부한다(<see cref="TryWrite"/> 가 false).
/// </para>
/// </summary>
public static class AuditLog
{
    public sealed record Entry(
        [property: JsonPropertyName("ts")] string Ts,
        [property: JsonPropertyName("tool")] string Tool,
        [property: JsonPropertyName("action")] string? Action,
        [property: JsonPropertyName("app")] string App,
        [property: JsonPropertyName("target")] string Target,
        [property: JsonPropertyName("hash")] string Hash,
        [property: JsonPropertyName("outcome")] string Outcome,
        [property: JsonPropertyName("approval")] string? Approval,
        [property: JsonPropertyName("reason")] string? Reason,
        [property: JsonPropertyName("mode")] string? Mode,
        [property: JsonPropertyName("user")] string User,
        [property: JsonPropertyName("host")] string Host,
        [property: JsonPropertyName("session")] string Session,
        [property: JsonPropertyName("pid")] int Pid,
        [property: JsonPropertyName("cwd")] string Cwd,
        [property: JsonPropertyName("tool_use_id")] string? ToolUseId);

    /// <summary>
    /// 결과 값. 오피스챗의 ok/error/declined 에 셸용 approved(승인 — 실행 전에 먼저 쓴다)·denied(사람 없음·규칙)·blocked(하드 차단)를
    /// 더했다. 승인된 호출은 approved 한 줄 + 실행 뒤 ok/error 한 줄(같은 tool_use_id)이 남는다 — 명령이 세션째 끊어도(sshd 재시작 등)
    /// 승인 기록은 이미 남아 있게.
    /// </summary>
    public static class Outcomes
    {
        public const string Approved = "approved";
        public const string Ok = "ok";
        public const string Error = "error";
        public const string Declined = "declined";
        public const string Denied = "denied";
        public const string Blocked = "blocked";
    }

    /// <summary>승인 방식 — 사람(user) / 배치 자동화·자동 승인(override) / 저장된 allow 규칙(rule) / 해당 없음(none: 거부·차단).</summary>
    public static class Approvals
    {
        public const string User = "user";
        public const string Override = "override";
        public const string Rule = "rule";
        public const string None = "none";
    }

    private static readonly object Gate = new();
    // tool_use_id 는 엔진마다 따로 매겨져 겹칠 수 있다(하위 에이전트의 call_0 과 부모의 call_0). 안쪽 호출이 먼저 끝나므로 스택으로 둔다.
    private static readonly ConcurrentDictionary<string, Stack<Entry>> Pending = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // 한글 대상 이름을 평문으로(오피스챗과 같음)
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>이 프로세스의 세션 id — 같은 실행에서 나온 기록을 묶는다.</summary>
    public static string SessionId { get; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>기록 파일 경로(테스트는 바꿀 수 있다).</summary>
    public static string FilePath { get; set; } = AppPaths.Combine("logs", "audit.jsonl");

    /// <summary>기록 한 건을 만든다. target 은 가린 뒤 저장된다.</summary>
    public static Entry Create(
        string tool, string target, string outcome, string approval, string? reason, string? mode, string? toolUseId,
        string? action = null)
    {
        var masked = Mask(target);
        return new Entry(
            DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture),
            tool, action, AppOf(tool), masked, Hash(masked), outcome, approval, reason, mode,
            Environment.UserName, Environment.MachineName, SessionId, Environment.ProcessId,
            Directory.GetCurrentDirectory(), toolUseId);
    }

    /// <summary>즉시 한 줄 쓴다. 성공하면 true.</summary>
    public static bool TryWrite(Entry entry)
    {
        try
        {
            var bytes = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(entry, Json) + "\n");
            lock (Gate)
            {
                using var fs = OpenExclusive(FileMode.Append, FileAccess.Write);
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            MoaiLog.Warn($"audit: write failed ({ex.GetType().Name})");
            return false;
        }
    }

    /// <summary>
    /// 승인된 호출을 기록한다 — 지금 approved 한 줄을 쓰고, 툴이 끝나면 <see cref="Complete"/> 가 ok/error 한 줄을 더 쓴다.
    /// 기록에 실패하면 false(확인 없이 승인하는 경로는 이때 거부한다).
    /// </summary>
    public static bool Hold(Entry entry)
    {
        if (!TryWrite(entry with { Outcome = Outcomes.Approved }))
        {
            return false;
        }

        if (entry.ToolUseId is { Length: > 0 } id)
        {
            var stack = Pending.GetOrAdd(id, _ => new Stack<Entry>());
            lock (stack)
            {
                stack.Push(entry);
            }
        }

        return true;
    }

    /// <summary>보류 중인 호출이면 결과를 채워 쓴다. 보류 대상이 아니면 아무것도 하지 않는다.</summary>
    public static void Complete(string toolUseId, bool isError)
    {
        if (!Pending.TryGetValue(toolUseId, out var stack))
        {
            return;
        }

        Entry? entry = null;
        lock (stack)
        {
            if (stack.Count > 0)
            {
                entry = stack.Pop();
            }

            if (stack.Count == 0)
            {
                Pending.TryRemove(toolUseId, out _);
            }
        }

        if (entry is not null)
        {
            TryWrite(entry with { Outcome = isError ? Outcomes.Error : Outcomes.Ok });
        }
    }

    /// <summary>보관일(일)을 넘긴 줄을 지운다. 0 이하 = 영구 보관. 시작할 때 한 번 부른다.</summary>
    public static void Trim(int days)
    {
        if (days <= 0)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                if (!File.Exists(FilePath))
                {
                    return;
                }

                // 다른 mate 프로세스(동시에 도는 배치)가 그 사이 덧붙인 줄을 잃지 않게, 파일을 독점으로 연 채 읽고 제자리에서 다시 쓴다.
                using var fs = OpenExclusive(FileMode.Open, FileAccess.ReadWrite);
                string all;
                using (var reader = new StreamReader(fs, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true))
                {
                    all = reader.ReadToEnd();
                }

                var lines = all.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var cutoff = DateTimeOffset.Now.AddDays(-days);
                var kept = lines.Where(l => !IsOlderThan(l, cutoff)).ToList();
                if (kept.Count == lines.Length)
                {
                    return;
                }

                var bytes = new UTF8Encoding(false).GetBytes(kept.Count == 0 ? string.Empty : string.Join('\n', kept) + "\n");
                fs.SetLength(0);
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
                MoaiLog.Info($"audit: trimmed {lines.Length - kept.Count} line(s) older than {days} day(s)");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MoaiLog.Warn($"audit: trim failed ({ex.GetType().Name})");
        }
    }

    // 프로세스 사이에서도 한 번에 하나만 쓰도록 독점(FileShare.None)으로 연다. 다른 프로세스가 쥐고 있으면 잠시 기다린다(최대 약 3초).
    // 연 뒤에는 0600 으로 맞춘다(이미 있던 파일의 권한이 넓어도 좁힌다).
    private static FileStream OpenExclusive(FileMode mode, FileAccess access)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var fs = new FileStream(FilePath, mode, access, FileShare.None);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                return fs;
            }
            catch (IOException) when (attempt < 60 && File.Exists(FilePath))
            {
                Thread.Sleep(50);
            }
        }
    }

    // 깨진 줄은 지우지 않는다(날짜를 모르면 보관).
    private static bool IsOlderThan(string line, DateTimeOffset cutoff)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("ts", out var ts)
                && DateTimeOffset.TryParse(ts.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var t)
                && t < cutoff;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string AppOf(string tool) => tool switch
    {
        "Bash" => "Shell",
        "Write" or "Edit" or "Read" => "File",
        _ when tool.StartsWith("mcp__", StringComparison.Ordinal) => "MCP",
        _ => "Tool",
    };

    private static string Hash(string target)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(target));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    // 명령 문자열 안의 비밀값을 가린다. 감사 로그에 비밀번호가 남으면 로그 자체가 유출 경로가 된다.
    private static readonly (Regex Pattern, string Replacement)[] Secrets =
    {
        (R(@"(?i)\b([A-Z0-9_]*(PASSWORD|PASSWD|PWD|SECRET|TOKEN|API_?KEY|ACCESS_?KEY|PRIVATE_?KEY)[A-Z0-9_]*)=(""[^""]*""|'[^']*'|\S+)"), "$1=***"),
        (R(@"(?i)((?<=^|\s)--?(password|passwd|pass|token|secret|api-key|access-key)(=|\s+))(""[^""]*""|'[^']*'|\S+)"), "$1***"),
        (R(@"(?i)\b(password|passwd|pwd|identified\s+by)(\s*[=:]\s*|\s+)(""[^""]*""|'[^']*'|\S+)"), "$1$2***"),
        (R(@"(?i)(authorization:\s*(bearer|basic|token)\s+)[A-Za-z0-9._~+/=-]+"), "$1***"),
        (R(@"(?i)(\b(bearer|token)\s+)[A-Za-z0-9._~+/=-]{16,}"), "$1***"),
        (R(@"(://[^/\s:@]+:)[^@\s]+@"), "$1***@"),
        (R(@"(\b(mysql|mariadb|mysqldump|mysqladmin)\b[^\n]*\s-p)(\S+)"), "$1***"),
        (R(@"(\b(mysql|mariadb|mysqldump|mysqladmin|sshpass|login)\b[^\n]*?\s-p\s+)(""[^""]*""|'[^']*'|\S+)"), "$1***"),
        (R(@"(\bredis-cli\b[^\n]*?\s-a\s+)(""[^""]*""|'[^']*'|\S+)"), "$1***"),
        (R(@"(\b(curl|wget)\b[^\n]*?\s(-u|--user|--http-password|--password)[=\s]+)(""[^""]*""|'[^']*'|\S+)"), "$1***"),
        (R(@"(?i)(-H\s*[""']?[\w-]*(key|token|secret|auth|cookie)[\w-]*:\s*)[^""'\s]+"), "$1***"),
        (R(@"(\becho\s+)(""[^""]*""|'[^']*'|\S+)(\s*\|\s*sudo\s+(-\S+\s+)*-S)"), "$1***$3"),
        (R(@"(\bhtpasswd\b[^\n]*\s-\w*b\w*\s+\S+\s+\S+\s+)(""[^""]*""|'[^']*'|\S+)"), "$1***"),
        (R(@"(?i)(""(password|passwd|pwd|secret|token|api_?key|access_?key)""\s*:\s*)""[^""]*"""), "$1\"***\""),
        (R(@"-----BEGIN ([A-Z ]+ )?PRIVATE KEY-----[\s\S]*?-----END ([A-Z ]+ )?PRIVATE KEY-----"), "-----PRIVATE KEY***-----"),
        // 주민등록번호(앞 6자리만 남김).
        (R(@"\b(\d{6})-?[1-8]\d{6}\b"), "$1-*******"),
    };

    private static readonly Regex PanCandidate = R(@"(?<!\d)(?:\d[ -]?){12,18}\d(?!\d)");

    /// <summary>비밀값·주민번호·카드번호(Luhn 이 맞는 13–19자리, 앞 6·뒤 4만 남김)를 가린다.</summary>
    public static string Mask(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "(none)";
        }

        var s = text;
        try
        {
            foreach (var (pattern, replacement) in Secrets)
            {
                s = pattern.Replace(s, replacement);
            }

            s = PanCandidate.Replace(s, m =>
            {
                var digits = new string(m.Value.Where(char.IsAsciiDigit).ToArray());
                return digits.Length is >= 13 and <= 19 && Luhn(digits)
                    ? digits[..6] + new string('*', digits.Length - 10) + digits[^4..]
                    : m.Value;
            });
        }
        catch (RegexMatchTimeoutException)
        {
            // 가리지 못한 원문은 남기지 않는다 — 길이만 남긴다.
            return $"(not recorded: masking timed out, {text.Length} chars)";
        }

        // 아주 긴 명령(heredoc 등)은 앞뒤만 남긴다 — 감사 로그 한 줄이 수 MB 가 되지 않게.
        return s.Length <= 4000 ? s : s[..3000] + $" …({s.Length - 3800} chars omitted)… " + s[^800..];
    }

    private static bool Luhn(string digits)
    {
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i] - '0';
            if (i % 2 == 1)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
        }

        return sum % 10 == 0;
    }

    private static Regex R(string p) => new(p, RegexOptions.None, TimeSpan.FromSeconds(1));
}
