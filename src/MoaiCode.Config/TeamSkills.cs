using System.Text;
using MoaiCode.Config;

namespace MoaiCode.Cli;

/// <summary>동기화 결과 — Written=기록한 스킬 수, Error=실패 사유(성공이면 null).</summary>
public sealed record TeamSkillsResult(int Written, string? Error);

/// <summary>
/// open-moai(vip) 서버에 팀 단위로 공유된 "스킬"을 로그인 계정으로 받아 ~/.moai/team-skills 에
/// SKILL.md 형태로 기록한다(SkillLoader 가 읽는 포맷). BundledSkills 와 동일하게 실패는 non-fatal —
/// 로컬/번들 스킬과 CLI 기동을 막지 않는다. 매 sync 마다 디렉터리를 재생성해 서버 상태와 일치시킨다.
/// </summary>
public static class TeamSkills
{
    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".moai", "team-skills");

    /// <summary>
    /// 로그인 계정으로 팀 공유 스킬을 받아 디스크에 기록한다. 미로그인/네트워크/인증/타임아웃 실패는
    /// 예외를 삼키고 Error 로 돌려준다(치명적이지 않음). orgId 는 primary/first 조직으로 자동 해소.
    /// </summary>
    public static async Task<TeamSkillsResult> SyncAsync(string baseUrl, string apiKey, CancellationToken ct)
    {
        try
        {
            var orgId = await OpenMoaiClient.ResolvePrimaryOrgIdAsync(baseUrl, apiKey, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(orgId))
            {
                return new TeamSkillsResult(0, null); // 소속 조직 없음 — 오류가 아니라 '받을 것 없음'.
            }

            var items = await OpenMoaiClient.GetSkillsAsync(baseUrl, apiKey, orgId!, ct).ConfigureAwait(false);

            // 서버 상태와 일치시키기 위해 매 sync 시 정리(삭제-재생성).
            if (Directory.Exists(Dir))
            {
                Directory.Delete(Dir, recursive: true);
            }

            Directory.CreateDirectory(Dir);

            var written = 0;
            foreach (var it in items)
            {
                var dirName = SafeDirName(it.Name);
                if (dirName.Length == 0)
                {
                    continue;
                }

                var skillDir = Path.Combine(Dir, dirName);
                Directory.CreateDirectory(skillDir);
                File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), ToSkillMd(it));
                written++;
            }

            return new TeamSkillsResult(written, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new TeamSkillsResult(0, "timeout"); // 짧은 타임아웃 초과 — non-fatal.
        }
        catch (Exception ex)
        {
            return new TeamSkillsResult(0, ex.Message); // 네트워크/인증/파싱 실패 — non-fatal.
        }
    }

    // SkillLoader.FrontmatterParser 가 읽는 포맷: --- name/description --- body.
    // name/description 은 단일 라인이어야 하므로 개행을 공백으로 평탄화한다. (public: 포맷 계약 테스트용)
    public static string ToSkillMd(TeamSkillItem it)
    {
        var name = Flatten(it.Name);
        var desc = Flatten(it.Description);
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("name: ").Append(name).Append('\n');
        sb.Append("description: ").Append(desc).Append('\n');
        sb.Append("---\n");
        sb.Append(it.Body);
        return sb.ToString();
    }

    private static string Flatten(string s) =>
        s.Replace("\r", " ").Replace("\n", " ").Trim();

    // 스킬 이름을 파일시스템 디렉터리명으로 안전화 — 경로 구분자/제어문자 차단, 상위 경로 탈출 방지.
    // (public: 경로 안전성 테스트용)
    public static string SafeDirName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.Trim())
        {
            sb.Append(c is '/' or '\\' or ':' || char.IsControl(c) ? '_' : c);
        }

        var s = sb.ToString().Trim().TrimStart('.');
        return s is "" or "." or ".." ? string.Empty : s;
    }
}
