using System.IO.Compression;

namespace MoaiCode.Cli;

/// <summary>
/// 바이너리에 임베드된 기본 스킬(awesome-claude-skills 큐레이션)을 첫 실행 시
/// ~/.moai/bundled-skills 로 추출한다. 사용자 스킬 디렉토리(~/.moai/skills 등)와 분리되어
/// 있어 버전 갱신 시 안전하게 재추출 가능. SkillLoader 가 이 경로를 최저 우선순위로 로드.
/// </summary>
public static class BundledSkills
{
    private const string Version = "1";              // 번들 내용 갱신 시 올림 → 재추출
    private const string ResourceName = "bundled-skills.zip";

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".moai", "bundled-skills");

    public static void EnsureExtracted()
    {
        try
        {
            var marker = Path.Combine(Dir, ".version");
            if (File.Exists(marker) && File.ReadAllText(marker).Trim() == Version)
            {
                return;
            }

            if (Directory.Exists(Dir))
            {
                Directory.Delete(Dir, recursive: true);
            }

            Directory.CreateDirectory(Dir);

            using var stream = typeof(BundledSkills).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                return;
            }

            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                zip.ExtractToDirectory(Dir, overwriteFiles: true);
            }

            File.WriteAllText(marker, Version);
        }
        catch
        {
            // 번들 추출 실패는 치명적이지 않다 — 스킬 없이도 동작.
        }
    }
}
