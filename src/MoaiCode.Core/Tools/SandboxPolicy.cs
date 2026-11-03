using System.Collections.Concurrent;

namespace MoaiCode.Core.Tools;

/// <summary>설정 sandbox: auto(기본) = 쓸 수 있으면 켠다, required = 반드시(못 쓰면 사람이 모든 명령을 승인), off = 끈다.</summary>
public enum SandboxMode
{
    Auto,
    Required,
    Off,
}

/// <summary>
/// 격리 실행 정책(2026-11-03 Bill 결정 "격리 실행 추가"). 명령을 읽어 위험을 판정하는 방식은 우회를 다 막을 수 없으므로,
/// <b>사람이 이 호출을 직접 승인하지 않은 셸 명령</b>(자동 승인 모드·allow 규칙·분류기 통과)은 격리 환경(bubblewrap)에서 돌린다 —
/// 작업 폴더·전용 /tmp 밖은 읽기 전용, 디스크 장치·호스트 프로세스·/run 의 제어 소켓(docker·systemd·tmux)은 보이지 않는다.
/// 판정이 틀려도 시스템은 바뀌지 않는다. 사람이 승인한 호출과 --ignore-confirm 배치는 격리하지 않는다.
/// 권한 게이트가 호출 id 로 "격리하지 않음"을 표시하고, BashTool 이 실행할 때 그 표시를 꺼내 쓴다.
/// </summary>
public static class SandboxPolicy
{
    private static readonly ConcurrentDictionary<string, byte> Unsandboxed = new(StringComparer.Ordinal);

    /// <summary>설정된 방식.</summary>
    public static SandboxMode Mode { get; set; } = SandboxMode.Off;

    /// <summary>격리 환경을 실제로 쓸 수 있는가(시작할 때 시험 실행으로 확인).</summary>
    public static bool Available { get; set; }

    /// <summary>격리해서 실행한다.</summary>
    public static bool Active => Mode != SandboxMode.Off && Available;

    /// <summary>반드시 격리해야 하는데 쓸 수 없다 — 사람이 승인하지 않은 셸 명령은 실행하지 않는다(모르면 묻는다).</summary>
    public static bool RequiredButUnavailable => Mode == SandboxMode.Required && !Available;

    /// <summary>격리 안에서 쓸 수 있는 폴더(작업 폴더 + 설정의 추가 경로 + 기본 캐시 폴더).</summary>
    public static IReadOnlyList<string> Writable { get; set; } = Array.Empty<string>();

    /// <summary>격리 안의 /tmp 로 쓸 이 세션 전용 폴더. 호스트 /tmp 의 소켓(tmux·ssh-agent 등)이 보이지 않게 한다.</summary>
    public static string? TmpDir { get; set; }

    /// <summary>사람이 승인한 호출(또는 --ignore-confirm) — 격리하지 않고 실행한다.</summary>
    public static void AllowUnsandboxed(string toolUseId)
    {
        if (toolUseId.Length > 0)
        {
            Unsandboxed[toolUseId] = 0;
        }
    }

    /// <summary>이 호출을 격리하지 않아도 되는가(한 번 꺼내면 지워진다).</summary>
    public static bool TakeUnsandboxed(string? toolUseId) =>
        toolUseId is { Length: > 0 } && Unsandboxed.TryRemove(toolUseId, out _);
}
