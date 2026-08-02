using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using MoaiCode.Core.Agent.Prompts;
using MoaiCode.Core.Tools;

namespace MoaiCode.Tools.Knowledge;

/// <summary>
/// 인증된 사용자(로그인한 API 키)의 소속 조직 목록을 조회한다. 문서함 툴(OrgDocs*)의 orgId 를
/// 확인·자동해소하는 출발점. 계약: GET {baseUrl}/organizations.
/// </summary>
public sealed class OrgListTool : ITool
{
    public string Name => "OrgList";

    public string Description => """
        Lists the organizations the logged-in user belongs to (id, name, role, whether it's the primary
        org). Use this to obtain the orgId needed by the knowledge base (조직 문서함) tools (OrgDocsList/OrgDocsUpload/
        OrgDocsDelete). Those tools also auto-resolve orgId from your login when you have exactly one
        organization, so you usually do NOT need to ask the user for it. Read-only, no arguments.
        """;

    public bool IsReadOnly => true;
    public bool IsConcurrencySafe => true;

    public JsonElement InputSchema { get; } = ToolSchema.Parse(
        """
        { "type": "object", "properties": {} }
        """);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        var baseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(key))
        {
            yield return new ToolOutput(
                "OrgList: open-moai 연결 정보가 없습니다. `moai login` 으로 로그인하세요.", IsError: true);
            yield break;
        }

        string? error = null;
        IReadOnlyList<OrgResolver.OrgInfo>? orgs = null;
        try
        {
            orgs = await OrgResolver.FetchAsync(baseUrl, key, ct).ConfigureAwait(false);
        }
        catch (OrgResolver.EndpointMissingException)
        {
            error = "OrgList: 이 서버에 조직 조회 엔드포인트(/api/v1/organizations)가 없습니다. " +
                    "open-moai 측 배포가 필요합니다.";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            error = "OrgList: 조직 조회 타임아웃";
        }
        catch (HttpRequestException ex)
        {
            error = $"OrgList: 요청 실패 — {ex.Message}";
        }

        if (error is not null)
        {
            yield return new ToolOutput(error, IsError: true);
            yield break;
        }

        if (orgs is null || orgs.Count == 0)
        {
            yield return new ToolOutput("(소속된 조직이 없습니다)");
            yield break;
        }

        var sb = new StringBuilder();
        sb.Append("소속 조직 ").Append(orgs.Count).AppendLine("개");
        sb.AppendLine();
        foreach (var o in orgs)
        {
            sb.Append("• [").Append(o.Id).Append("] ").Append(o.Name ?? "(이름 없음)");
            var meta = new List<string>();
            if (!string.IsNullOrWhiteSpace(o.Role)) meta.Add(o.Role!);
            if (o.IsPrimary == true) meta.Add("기본");
            if (meta.Count > 0) sb.Append("  (").Append(string.Join(" · ", meta)).Append(')');
            sb.AppendLine();
        }

        // 조직명은 외부 데이터 — 신뢰불가 경계로 감싼다.
        yield return new ToolOutput(Reminders.UntrustedToolOutput + sb.ToString().TrimEnd());
    }
}
