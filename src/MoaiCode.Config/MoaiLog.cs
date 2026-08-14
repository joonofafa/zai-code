using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MoaiCode.Config;

public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
    Fatal = 5,
    Off = 6,
}

/// <summary>
/// 공용 레벨별 파일 로거. ~/.moai/logs/moai.log 에 기록한다.
/// 레벨은 ~/.moai/settings.json 의 "logLevel" 로 제어(기본 Info).
/// 스레드/프로세스가 죽더라도 즉시 append 되므로 "어떤 식으로든" 로그가 남는다.
/// </summary>
public static class MoaiLog
{
    private static readonly object Gate = new();
    private const long MaxBytes = 5 * 1024 * 1024; // 5MB 넘으면 .1 로 롤오버

    /// <summary>이 레벨 미만은 기록하지 않는다. Configure 로 설정 전 기본값은 Info.</summary>
    public static LogLevel MinLevel { get; set; } = LogLevel.Info;

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".moai", "logs", "moai.log");

    public static LogLevel Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "trace" => LogLevel.Trace,
        "debug" => LogLevel.Debug,
        "info" or "information" => LogLevel.Info,
        "warn" or "warning" => LogLevel.Warn,
        "error" => LogLevel.Error,
        "fatal" or "critical" => LogLevel.Fatal,
        "off" or "none" or "silent" => LogLevel.Off,
        _ => LogLevel.Info,
    };

    /// <summary>settings.json 의 logLevel 문자열로 최소 레벨을 설정한다.</summary>
    public static void Configure(string? level)
    {
        if (!string.IsNullOrWhiteSpace(level))
        {
            MinLevel = Parse(level);
        }
    }

    // UDP 실시간 로그 트레이스(디버깅용). 각 로그 라인을 지정 호스트:포트로 UDP 전송한다.
    // 기본 OFF — 설정("udpLog") 또는 env(MOAI_UDP_LOG)로 명시할 때만 켜진다.
    // ⚠️ 개발/사내 디버깅 전용. 고객 배포본에서는 절대 활성화하지 말 것(LAN 로그 유출).
    private static UdpClient? _udp;
    private static IPEndPoint? _udpEndpoint;

    /// <summary>UDP 로그 대상 설정. "host" 또는 "host:port"(기본 포트 5599). null/빈값이면 비활성.</summary>
    public static void ConfigureUdp(string? target)
    {
        _udp = null;
        _udpEndpoint = null;
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        try
        {
            var t = target.Trim();
            var colon = t.LastIndexOf(':');
            var host = colon > 0 ? t[..colon] : t;
            var port = colon > 0 && int.TryParse(t[(colon + 1)..], out var p) ? p : 5599;
            var ip = IPAddress.TryParse(host, out var parsed)
                ? parsed
                : Dns.GetHostAddresses(host).FirstOrDefault() ?? throw new InvalidOperationException("host not resolvable");
            _udpEndpoint = new IPEndPoint(ip, port);
            _udp = new UdpClient();
            Info($"UDP log trace enabled -> {host}:{port}");
        }
        catch
        {
            _udp = null;
            _udpEndpoint = null; // 설정 실패는 무시(파일 로깅은 계속).
        }
    }

    private static void SendUdp(string line)
    {
        var udp = _udp;
        var ep = _udpEndpoint;
        if (udp is null || ep is null)
        {
            return;
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            udp.Send(bytes, bytes.Length, ep); // 비연결 fire-and-forget.
        }
        catch
        {
            // UDP 전송 실패는 앱/로깅에 영향 없음.
        }
    }

    // ── UDP 전용 상세/콘텐츠 트레이스 ──
    // 파일 로그에는 기록하지 않는다(원문/한글이 영속 로그를 오염시키지 않게 — CLAUDE.md). UDP 미설정 시 no-op.
    // 디버깅 목적: 프롬프트/답변/툴 파라미터·결과/생성 파일을 실시간 UDP 수신기로 흘려보낸다.
    private const int UdpTextCap = 16000;
    private static int _blobSeq;

    /// <summary>UDP 전용 상세 트레이스(파일 미기록). 긴 내용은 앞부분만 전송.</summary>
    public static void Udp(string message)
    {
        if (_udp is null || _udpEndpoint is null || string.IsNullOrEmpty(message))
        {
            return;
        }

        var msg = message.Length > UdpTextCap
            ? message[..UdpTextCap] + $"...(+{message.Length - UdpTextCap} more)"
            : message;
        SendUdp($"{DateTimeOffset.Now:HH:mm:ss.fff} [TRACE] {msg}");
    }

    /// <summary>바이트(예: 생성 문서)를 base64 청크로 UDP 전송. 수신기가 name+id 로 재조립·복원한다.
    /// 파일 로그에는 ASCII 메타(이름·크기·청크수)만 남긴다. UDP 미설정 시 no-op.</summary>
    public static void UdpBlob(string tag, byte[] data)
    {
        if (_udp is null || _udpEndpoint is null || data is null || data.Length == 0)
        {
            return;
        }

        var b64 = Convert.ToBase64String(data);
        const int chunk = 8000; // datagram 당 base64 문자수(UDP 상한 여유).
        var total = (b64.Length + chunk - 1) / chunk;
        var id = System.Threading.Interlocked.Increment(ref _blobSeq);
        var safe = tag.Replace(' ', '_').Replace('|', '_').Replace('\n', '_');
        for (var i = 0; i < total; i++)
        {
            var part = b64.Substring(i * chunk, Math.Min(chunk, b64.Length - i * chunk));
            SendUdp($"[FILE] id={id} name={safe} {i + 1}/{total} {part}");
        }

        Info($"UDP file sent: {safe} ({data.Length} bytes, {total} chunks)"); // 파일 로그엔 메타만(ASCII).
    }

    public static void Trace(string message) => Write(LogLevel.Trace, message, null);
    public static void Debug(string message) => Write(LogLevel.Debug, message, null);
    public static void Info(string message) => Write(LogLevel.Info, message, null);
    public static void Warn(string message) => Write(LogLevel.Warn, message, null);
    public static void Error(string message, Exception? ex = null) => Write(LogLevel.Error, message, ex);
    public static void Fatal(string message, Exception? ex = null) => Write(LogLevel.Fatal, message, ex);

    private static void Write(LogLevel level, string message, Exception? ex)
    {
        if (level < MinLevel || MinLevel == LogLevel.Off)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                RollIfTooLarge();

                var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level.ToString().ToUpperInvariant(),-5}] {message}";
                if (ex is not null)
                {
                    line += Environment.NewLine + ex;
                }

                File.AppendAllText(FilePath, line + Environment.NewLine);
                SendUdp(line);
            }
        }
        catch
        {
            // 로깅 실패는 앱 동작을 막지 않는다.
        }
    }

    private static void RollIfTooLarge()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (info.Exists && info.Length > MaxBytes)
            {
                var rolled = FilePath + ".1";
                if (File.Exists(rolled))
                {
                    File.Delete(rolled);
                }

                File.Move(FilePath, rolled);
            }
        }
        catch
        {
            // 롤오버 실패 시 그냥 계속 append.
        }
    }
}
