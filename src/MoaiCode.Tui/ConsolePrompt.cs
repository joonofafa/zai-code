namespace MoaiCode.Tui;

/// <summary>
/// 콘솔 입력 대기 상태 공유 플래그. 권한 다이얼로그/선택 툴이 사용자 입력을 기다리는 동안
/// true 로 두면, 하단 스피너가 그 줄을 덮어쓰지 않도록 멈춘다. (단일 프로세스·순차 실행 전제)
/// </summary>
public static class ConsolePrompt
{
    public static bool IsPrompting { get; private set; }

    /// <summary>using 스코프 동안 IsPrompting 를 true 로 유지.</summary>
    public static IDisposable Begin()
    {
        IsPrompting = true;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => IsPrompting = false;
    }
}
