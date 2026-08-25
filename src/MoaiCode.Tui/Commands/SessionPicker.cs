using System.Text;
using MoaiCode.Localization;

namespace MoaiCode.Tui.Commands;

/// <summary>세션 선택/복원 공용 로직. 대화형이면 화살표 picker, 비대화형이면 텍스트 목록.</summary>
internal static class SessionPicker
{
    public static async Task<SlashResult> RunAsync(SlashContext ctx, CancellationToken ct)
    {
        var infos = await ctx.Sessions.ListInfosAsync(ct).ConfigureAwait(false);
        if (infos.Count == 0)
        {
            return new SlashResult(L10n.Get("session.none"));
        }

        // 메시지 수 자리수를 목록 최대값에 맞춰 정렬.
        var countWidth = infos.Max(i => i.MessageCount).ToString().Length;

        // 비대화형(파이프/테스트): 텍스트 목록만 보여주고 /resume <번호|id> 안내.
        if (Console.IsInputRedirected)
        {
            var sb = new StringBuilder();
            sb.AppendLine(L10n.Get("session.listHeader"));
            for (var i = 0; i < infos.Count; i++)
            {
                sb.AppendLine($"  {i + 1:00}. {Label(infos[i], countWidth)}");
                sb.AppendLine($"      {infos[i].Id}");
            }

            return new SlashResult(sb.ToString().TrimEnd());
        }

        // infos 는 Del 삭제로 변형되므로 가변 사본으로 다룬다(SelectList 내부 목록과 같은 index 를 지워 정합 유지).
        var rows = infos.ToList();
        var labels = rows.Select(s => Label(s, countWidth)).ToList();
        // 번호를 2자리(01.~99.)로 줄맞춤 — 목록은 최대 99개.
        var numberWidth = Math.Max(2, rows.Count.ToString().Length);
        var pick = SelectList.Prompt(
            L10n.Get("session.pickTitle"), labels, numberWidth: numberWidth,
            onDelete: i =>
            {
                var ok = ctx.Sessions.Delete(rows[i].Id);
                if (ok)
                {
                    rows.RemoveAt(i);
                }

                return ok;
            });
        if (pick < 0)
        {
            // rows 는 진입 시 비어 있지 않았다(위에서 early-return). 지금 비었다면 Del 로 전부 지운 것 —
            // Esc 취소와 구분해 "모두 삭제됨"으로 알린다.
            return new SlashResult(L10n.Get(rows.Count == 0 ? "session.allDeleted" : "common.cancelled"));
        }

        return await ResumeAsync(ctx, rows[pick].Id, ct).ConfigureAwait(false);
    }

    public static async Task<SlashResult> ResumeAsync(SlashContext ctx, string id, CancellationToken ct)
    {
        var loaded = await ctx.Sessions.LoadAsync(id, ct).ConfigureAwait(false);
        if (loaded.Count == 0)
        {
            return new SlashResult(L10n.Get("session.notFound", id));
        }

        ctx.Engine.Restore(loaded);

        // 복원된 이전 대화를 화면에 다시 보여준다 (비대화형에서는 생략).
        if (!Console.IsInputRedirected)
        {
            TranscriptRenderer.Render(loaded);
        }

        return new SlashResult(L10n.Get("session.restored", id, loaded.Count));
    }

    private static string Label(MoaiCode.Persistence.SessionInfo s, int countWidth)
    {
        var title = string.IsNullOrWhiteSpace(s.Title) ? L10n.Get("session.untitled") : s.Title;
        if (title.Length > 50)
        {
            title = title[..50] + "…";
        }

        var count = s.MessageCount.ToString().PadLeft(countWidth);
        return L10n.Get("session.row", s.ModifiedAt.ToString("MM-dd HH:mm"), count, title);
    }
}
