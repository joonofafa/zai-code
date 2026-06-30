using System.Text.Json;

namespace MoaiCode.Core.Tools;

public enum PermissionMode { Ask, Auto, Deny }

/// <summary>
/// 세션 동안 Read된 파일을 추적 (read-before-edit 강제용). null이면 강제 안 함(직접 호출/테스트).
/// </summary>
public sealed class ReadTracker
{
    private readonly HashSet<string> _read = new(StringComparer.Ordinal);

    public void MarkRead(string path) => _read.Add(Normalize(path));

    public bool WasRead(string path) => _read.Contains(Normalize(path));

    private static string Normalize(string p)
    {
        try
        {
            return Path.GetFullPath(p);
        }
        catch
        {
            return p;
        }
    }
}

/// <summary>툴 실행 컨텍스트 (TS의 ToolUseContext 축약판).</summary>
public sealed record ToolContext(
    string WorkingDirectory,
    PermissionMode Permission,
    ReadTracker? Reads = null);

/// <summary>툴 실행 중 스트리밍되는 진행 이벤트.</summary>
public abstract record ToolProgress;

public sealed record ToolOutput(string Text, bool IsError = false) : ToolProgress;

public sealed record ToolStatus(string Message) : ToolProgress;

/// <summary>
/// 모든 툴의 계약. TS의 Tool 인터페이스에서 React 렌더 메서드를 제거하고
/// 순수 데이터(ToolProgress)만 스트리밍하도록 단순화 (렌더링은 TUI 레이어 책임).
/// 입력 스키마는 JSON Schema 단일 소스 — LLM 전송용/런타임 검증용 공유.
/// </summary>
public interface ITool
{
    string Name { get; }
    string Description { get; }
    JsonElement InputSchema { get; }
    bool IsReadOnly { get; }
    bool IsConcurrencySafe { get; }

    IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input,
        ToolContext context,
        CancellationToken ct);
}
