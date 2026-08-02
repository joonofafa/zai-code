using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace MoaiCode.Localization;

/// <summary>
/// 프로세스 공용 문자열 로컬라이저.
/// InvariantGlobalization 빌드에서도 동작하도록 CultureInfo/위성 어셈블리에 의존하지 않고
/// 명시적인 언어 코드와 임베드 JSON 카탈로그를 사용한다.
/// </summary>
public static class L10n
{
    public const string DefaultLanguage = "ko";
    public const string FallbackLanguage = "en";

    private static readonly IReadOnlyList<LanguageOption> LanguageList =
    [
        new("ko", "한국어"),
        new("en", "English"),
    ];

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Catalogs =
        LanguageList.ToDictionary(
            language => language.Code,
            language => LoadCatalog(language.Code),
            StringComparer.OrdinalIgnoreCase);

    private static string _currentLanguage = DefaultLanguage;

    public static event Action? LanguageChanged;

    public static IReadOnlyList<LanguageOption> SupportedLanguages => LanguageList;

    public static string CurrentLanguage => Volatile.Read(ref _currentLanguage);

    /// <summary>ko-KR, ko_KR처럼 지역이 붙은 코드도 기본 언어 코드로 정규화한다.</summary>
    public static string? NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var code = language.Trim().Replace('_', '-');
        var separator = code.IndexOf('-');
        if (separator > 0)
        {
            code = code[..separator];
        }

        return LanguageList.Any(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase))
            ? code.ToLowerInvariant()
            : null;
    }

    /// <summary>지원 언어면 전환하고 true를 반환한다. 미지원 코드는 현재 언어를 바꾸지 않는다.</summary>
    public static bool SetLanguage(string? language)
    {
        var normalized = NormalizeLanguage(language);
        if (normalized is null)
        {
            return false;
        }

        var previous = Interlocked.Exchange(ref _currentLanguage, normalized);
        if (!string.Equals(previous, normalized, StringComparison.Ordinal))
        {
            LanguageChanged?.Invoke();
        }

        return true;
    }

    public static string Get(string key, params object?[] args)
        => GetForLanguage(CurrentLanguage, key, args);

    /// <summary>전역 언어를 바꾸지 않고 특정 언어의 문자열을 조회한다. 테스트·미리보기에 사용.</summary>
    public static string GetForLanguage(string? language, string key, params object?[] args)
    {
        var normalized = NormalizeLanguage(language) ?? FallbackLanguage;
        var template = Lookup(normalized, key)
                       ?? Lookup(FallbackLanguage, key)
                       ?? key;

        return args.Length == 0
            ? template
            : string.Format(CultureInfo.InvariantCulture, template, args);
    }

    public static string GetLanguageDisplayName(string code)
        => LanguageList.FirstOrDefault(
               x => string.Equals(x.Code, NormalizeLanguage(code), StringComparison.OrdinalIgnoreCase))
           ?.DisplayName ?? code;

    /// <summary>번역 카탈로그 완전성 검사와 진단용 키 목록.</summary>
    public static IReadOnlyCollection<string> GetKeys(string language)
    {
        var normalized = NormalizeLanguage(language);
        return normalized is not null && Catalogs.TryGetValue(normalized, out var catalog)
            ? catalog.Keys.ToArray()
            : Array.Empty<string>();
    }

    private static string? Lookup(string language, string key)
        => Catalogs.TryGetValue(language, out var catalog) && catalog.TryGetValue(key, out var value)
            ? value
            : null;

    private static IReadOnlyDictionary<string, string> LoadCatalog(string language)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"{typeof(L10n).Namespace}.Resources.{language}.json";
        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException($"Missing localization resource: {resourceName}");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
               ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
