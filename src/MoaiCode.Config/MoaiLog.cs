using System;
using System.IO;

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
