using System.Text;

namespace MoaiCode.Tui.Commands;

/// <summary>세션 선택/복원 공용 로직. 대화형이면 화살표 picker, 비대화형이면 텍스트 목록.</summary>
internal static class SessionPicker
{
    public static async Task<SlashResult> RunAsync(SlashContext ctx, CancellationToken ct)
    {
        var infos = await ctx.Sessions.ListInfosAsync(ct).ConfigureAwait(false);
        if (infos.Count == 0)
        {
            return new SlashResult("저장된 세션이 없습니다.");
        }

        // 비대화형(파이프/테스트): 텍스트 목록만 보여주고 /resume <번호|id> 안내.
        if (Console.IsInputRedirected)
        {
            var sb = new StringBuilder();
            sb.AppendLine("저장된 세션 (최근 순) — /resume <번호> 또는 /resume <id>:");
            for (var i = 0; i < infos.Count; i++)
            {
                sb.AppendLine($"  {i + 1,2}. {Label(infos[i])}");
                sb.AppendLine($"      {infos[i].Id}");
            }

            return new SlashResult(sb.ToString().TrimEnd());
        }

        var labels = infos.Select(Label).ToList();
        var pick = SelectList.Prompt("저장된 세션 — 복원할 세션을 고르세요:", labels);
        if (pick < 0)
        {
            return new SlashResult("(취소됨)");
        }

        return await ResumeAsync(ctx, infos[pick].Id, ct).ConfigureAwait(false);
    }

    public static async Task<SlashResult> ResumeAsync(SlashContext ctx, string id, CancellationToken ct)
    {
        var loaded = await ctx.Sessions.LoadAsync(id, ct).ConfigureAwait(false);
        if (loaded.Count == 0)
        {
            return new SlashResult($"세션 없음 또는 비어있음: {id}");
        }

        ctx.Engine.Restore(loaded);

        // 복원된 이전 대화를 화면에 다시 보여준다 (비대화형에서는 생략).
        if (!Console.IsInputRedirected)
        {
            TranscriptRenderer.Render(loaded);
        }

        return new SlashResult($"복원됨: {id} ({loaded.Count} messages) — 위 대화에서 이어집니다");
    }

    private static string Label(MoaiCode.Persistence.SessionInfo s)
    {
        var title = string.IsNullOrWhiteSpace(s.Title) ? "(제목 없음)" : s.Title;
        if (title.Length > 50)
        {
            title = title[..50] + "…";
        }

        return $"{s.ModifiedAt:MM-dd HH:mm}  {s.MessageCount,3}개  {title}";
    }
}
