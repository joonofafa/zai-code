namespace MoaiCode.Core;

/// <summary>
/// 데이터 루트(~/.zaicode)의 유일한 조립 지점. 설정·로그·세션·메모리·자격증명 등은 모두 <see cref="Combine"/> 으로 만든다.
/// MOAI_HOME 이 있으면 그 경로가 루트다 — 테스트 격리용. HOME 을 바꾸는 것으로는 Windows 에서 격리되지 않는다
/// (SpecialFolder.UserProfile 이 HOME 을 보지 않음).
/// 사용자 홈 자체(셸 cwd, ~ 펼치기, ~/.claude 상속)는 데이터 루트가 아니므로 <see cref="UserHome"/> 을 쓴다.
/// </summary>
public static class AppPaths
{
    public const string HomeEnvVar = "MOAI_HOME";

    private const string DataDirName = ".zaicode";

    /// <summary>데이터 루트. 반드시 매번 계산한다 — 정적 초기화로 굳히면 이후 MOAI_HOME 을 세운 테스트가 진짜 홈에 쓴다.</summary>
    public static string Home
    {
        get
        {
            var env = Environment.GetEnvironmentVariable(HomeEnvVar);
            return !string.IsNullOrWhiteSpace(env)
                ? Path.GetFullPath(env.Trim())
                : Path.Combine(UserHome, DataDirName);
        }
    }

    /// <summary>데이터 루트 아래 경로.</summary>
    public static string Combine(params string[] parts) => Path.Combine([Home, .. parts]);

    /// <summary>사용자 홈 그 자체(데이터 루트가 아니다).</summary>
    public static string UserHome => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
