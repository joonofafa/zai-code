namespace MoaiCode.Core;

/// <summary>
/// 데이터 루트(~/.mate)의 유일한 조립 지점. 설정·로그·세션·메모리·자격증명 등은 모두 <see cref="Combine"/> 으로 만든다.
/// MOAI_HOME 이 있으면 그 경로가 루트다 — 테스트 격리용. HOME 을 바꾸는 것으로는 Windows 에서 격리되지 않는다
/// (SpecialFolder.UserProfile 이 HOME 을 보지 않음).
/// 사용자 홈 자체(셸 cwd, ~ 펼치기, ~/.claude 상속)는 데이터 루트가 아니므로 <see cref="UserHome"/> 을 쓴다.
/// </summary>
public static class AppPaths
{
    public const string HomeEnvVar = "MOAI_HOME";

    private const string DataDirName = ".mate";

    /// <summary>1.3.x 까지의 이름(Z.ai Code 시절). 시작할 때 <see cref="MigrateLegacyHome"/> 가 새 이름으로 옮긴다.</summary>
    private const string LegacyDataDirName = ".zaicode";

    /// <summary>데이터 루트. 반드시 매번 계산한다 — 정적 초기화로 굳히면 이후 MOAI_HOME 을 세운 테스트가 진짜 홈에 쓴다.</summary>
    public static string Home
    {
        get
        {
            var env = Environment.GetEnvironmentVariable(HomeEnvVar);
            return !string.IsNullOrWhiteSpace(env)
                ? Path.GetFullPath(env.Trim())
                : HomeUnder(UserHome);
        }
    }

    /// <summary>
    /// 사용자 홈 아래의 데이터 루트. 새 폴더가 없고 옛 폴더만 있으면 옛 폴더를 쓴다 — 이전이 실패해도
    /// (Windows 에서 다른 프로세스가 파일을 잡고 있는 등) 설정·키가 빈 새 폴더로 바뀌어 사라진 것처럼 보이지 않게.
    /// </summary>
    internal static string HomeUnder(string userHome)
    {
        var current = Path.Combine(userHome, DataDirName);
        if (!Directory.Exists(current))
        {
            var legacy = Path.Combine(userHome, LegacyDataDirName);
            if (Directory.Exists(legacy))
            {
                return legacy;
            }
        }

        return current;
    }

    /// <summary>
    /// 옛 데이터 루트(~/.zaicode)를 새 이름(~/.mate)으로 옮긴다. 옮겼으면 true.
    /// 옛 자리에는 새 폴더를 가리키는 심볼릭 링크를 남긴다 — 아직 떠 있는 옛 판 프로세스와 그 경로를 읽는 외부 도구가
    /// 계속 같은 곳을 보게(링크를 못 만들면 건너뛴다). MOAI_HOME 을 지정했거나, 새 폴더가 이미 있거나, 옛 자리가
    /// 이미 링크면 아무것도 하지 않는다.
    /// </summary>
    public static bool MigrateLegacyHome()
        => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(HomeEnvVar)) && MigrateLegacyHome(UserHome);

    internal static bool MigrateLegacyHome(string userHome)
    {
        var legacy = new DirectoryInfo(Path.Combine(userHome, LegacyDataDirName));
        var current = Path.Combine(userHome, DataDirName);
        try
        {
            if (!legacy.Exists || legacy.LinkTarget is not null || Path.Exists(current))
            {
                return false;
            }

            Directory.Move(legacy.FullName, current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;   // 동시에 뜬 다른 프로세스가 먼저 옮겼거나 파일이 잠김 — HomeUnder 가 옛 폴더로 이어 간다.
        }

        try
        {
            Directory.CreateSymbolicLink(legacy.FullName, DataDirName);   // 상대 경로: 홈 폴더를 옮겨도 깨지지 않는다
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Windows 는 개발자 모드가 아니면 링크 권한이 없다. 이전 자체는 끝났다.
        }

        return true;
    }

    /// <summary>데이터 루트 아래 경로.</summary>
    public static string Combine(params string[] parts) => Path.Combine([Home, .. parts]);

    /// <summary>사용자 홈 그 자체(데이터 루트가 아니다).</summary>
    public static string UserHome => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
