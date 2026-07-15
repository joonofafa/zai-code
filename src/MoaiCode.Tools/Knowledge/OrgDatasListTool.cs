using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoaiCode.Core.Agent.Prompts;
using MoaiCode.Core.Tools;

namespace MoaiCode.Tools.Knowledge;

/// <summary>
/// open-moai 데이터함(Record DB)에서 조회 가능한 테이블 목록과 스키마(컬럼/타입/샘플)를 가져온다.
/// LLM 이 OrgDatas 로 SELECT 를 쓰기 전에 이 툴로 테이블 구조를 먼저 파악한다.
/// 계약: docs/SERVER_TASK_RECORD_DATA.md §3.
/// </summary>
public sealed class OrgDatasListTool : ITool
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public string Name => "OrgDatasList";

    public string Description => """
        Lists the queryable data tables (Record DB collections) in the connected open-moai account,
        WITH their schema (columns, types, sample values). Call this FIRST — before OrgDatas — to
        learn what tables and columns exist so you can write a correct read-only SELECT for OrgDatas.
        Use when the user asks about tabular/record data (sales, metrics, records) rather than documents.
        """;

    public bool IsReadOnly => true;
    public bool IsConcurrencySafe => true;

    public JsonElement InputSchema { get; } = ToolSchema.Parse("""{ "type": "object", "properties": {} }""");

    private sealed record ColumnInfo(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("sample")] List<JsonElement>? Sample);

    private sealed record CollectionInfo(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("table_name")] string? TableName,
        [property: JsonPropertyName("totalRecords")] long? TotalRecords,
        [property: JsonPropertyName("columns")] List<ColumnInfo>? Columns);

    private sealed record ListResponse(
        [property: JsonPropertyName("collections")] List<CollectionInfo>? Collections,
        [property: JsonPropertyName("count")] int? Count);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        var baseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(key))
        {
            yield return new ToolOutput(
                "OrgDatasList: open-moai 연결 정보가 없습니다. `moai login` 으로 로그인하세요.", IsError: true);
            yield break;
        }

        var url = baseUrl.TrimEnd('/') + "/record-collections";

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(Timeout);

        string? error = null;
        ListResponse? data = null;
        try
        {
            data = await CallAsync(url, key, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (EndpointMissingException)
        {
            error = "OrgDatasList: 이 서버에 데이터함 엔드포인트(/api/v1/record-collections)가 없습니다. " +
                    "open-moai 측에 배포가 필요합니다 (docs/SERVER_TASK_RECORD_DATA.md).";
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            error = $"OrgDatasList: {Timeout.TotalSeconds:0}초 타임아웃";
        }
        catch (HttpRequestException ex)
        {
            error = $"OrgDatasList: 요청 실패 — {ex.Message}";
        }

        if (error is not null)
        {
            yield return new ToolOutput(error, IsError: true);
            yield break;
        }

        var cols = data?.Collections ?? new List<CollectionInfo>();
        if (cols.Count == 0)
        {
            yield return new ToolOutput("(조회 가능한 데이터 테이블이 없습니다)");
            yield break;
        }

        var sb = new StringBuilder();
        foreach (var c in cols)
        {
            sb.Append("■ ").Append(c.Name ?? c.TableName ?? "(unnamed)");
            if (!string.IsNullOrWhiteSpace(c.TableName))
            {
                sb.Append("  [table: ").Append(c.TableName).Append(']');
            }

            if (c.TotalRecords is { } n)
            {
                sb.Append("  (~").Append(n.ToString("N0")).Append("건)");
            }

            sb.AppendLine();
            foreach (var col in c.Columns ?? new List<ColumnInfo>())
            {
                sb.Append("   - ").Append(col.Name).Append(" : ").Append(col.Type ?? "?");
                if (col.Sample is { Count: > 0 } s)
                {
                    sb.Append("  예) ").Append(string.Join(", ", s.Take(3).Select(v => v.ToString())));
                }

                sb.AppendLine();
            }

            sb.AppendLine();
        }

        // 외부 데이터 — 신뢰불가 경계로 감싼다(스키마/샘플에 인젝션 문자열이 있을 수 있음).
        yield return new ToolOutput(Reminders.UntrustedToolOutput + sb.ToString().TrimEnd());
    }

    private static async Task<ListResponse?> CallAsync(string url, string key, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);

        using var resp = await client.GetAsync(url, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            throw new EndpointMissingException();
        }

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {body.Trim()}");
        }

        return await resp.Content.ReadFromJsonAsync<ListResponse>(cancellationToken: ct).ConfigureAwait(false);
    }

    private sealed class EndpointMissingException : Exception
    {
    }
}
