using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoaiCode.Core.Agent.Prompts;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;

namespace MoaiCode.Tools.Knowledge;

/// <summary>
/// open-moai 데이터함(Record DB)에 읽기전용 SELECT 를 실행해 표(columns/rows)로 받는다.
/// LLM 은 먼저 OrgDatasList 로 스키마를 얻고, 그 스키마 기반 SELECT 를 여기서 실행한다.
/// 읽기전용은 서버가 최종 강제하지만, 클라이언트도 비-SELECT 를 미리 거부한다(빠른 실패 + 다중 방어).
/// 계약: docs/SERVER_TASK_RECORD_DATA.md §4.
/// </summary>
public sealed class OrgDatasTool : ITool
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    // 클라이언트 선차단용 읽기전용 허용 키워드(서버 ALLOWED_KEYWORDS 와 정합). WITH = CTE 로 시작하는 SELECT.
    private static readonly HashSet<string> ReadOnlyStart = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "WITH", "EXPLAIN", "SHOW", "DESCRIBE", "DESC", "PRAGMA",
    };

    public string Name => "OrgDatas";

    public string Description => """
        Runs a READ-ONLY SQL SELECT against a data table in the organization data warehouse and
        returns the result as a table. First call OrgDatasList to get table names, columns, and
        sample values, then write a SELECT here. Only SELECT/WITH/EXPLAIN/SHOW/DESCRIBE/PRAGMA are
        allowed (no INSERT/UPDATE/DELETE/DROP). Give the collection's id (from OrgDatasList) and the SQL.
        For a chart or spreadsheet from the result, follow up with XlsxCreate.
        """;

    public bool IsReadOnly => true;
    public bool IsConcurrencySafe => true;

    public JsonElement InputSchema { get; } = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "collection_id": { "type": "string", "description": "Table/collection id from OrgDatasList" },
            "sql": { "type": "string", "description": "Read-only SELECT statement" },
            "maxRows": { "type": "integer", "description": "Max rows (default 100, max 500)" }
          },
          "required": ["collection_id", "sql"]
        }
        """);

    private sealed record Input(
        [property: JsonPropertyName("collection_id")] string? CollectionId,
        [property: JsonPropertyName("sql")] string? Sql,
        [property: JsonPropertyName("maxRows")] int? MaxRows);

    private sealed record QueryResponse(
        [property: JsonPropertyName("columns")] List<string>? Columns,
        [property: JsonPropertyName("rows")] List<List<JsonElement>>? Rows,
        [property: JsonPropertyName("rowCount")] int? RowCount,
        [property: JsonPropertyName("truncated")] bool? Truncated,
        // 서버가 구조적 데이터 대신 markdown 만 줄 때의 폴백.
        [property: JsonPropertyName("markdown_table")] string? MarkdownTable);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        var inp = input.Deserialize<Input>();
        if (inp is null || string.IsNullOrWhiteSpace(inp.CollectionId) || string.IsNullOrWhiteSpace(inp.Sql))
        {
            yield return new ToolOutput(L10n.Get("tools.orgDatas.inputRequired"), IsError: true);
            yield break;
        }

        if (!IsReadOnlySql(inp.Sql))
        {
            yield return new ToolOutput(
                L10n.Get("tools.orgDatas.readOnly"),
                IsError: true);
            yield break;
        }

        var baseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(key))
        {
            yield return new ToolOutput(
                L10n.Get("tools.orgDatas.notLoggedIn"), IsError: true);
            yield break;
        }

        var url = baseUrl.TrimEnd('/') + "/record-collections/query";

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(Timeout);

        string? error = null;
        QueryResponse? data = null;
        try
        {
            data = await CallAsync(url, key, inp, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (EndpointMissingException)
        {
            error = L10n.Get("tools.orgDatas.endpointMissing");
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            error = L10n.Get("tools.orgDatas.timeout", Timeout.TotalSeconds);
        }
        catch (HttpRequestException ex)
        {
            error = L10n.Get("tools.orgDatas.requestFailed", ex.Message);
        }

        if (error is not null)
        {
            yield return new ToolOutput(error, IsError: true);
            yield break;
        }

        // 데이터도 외부 콘텐츠 — 신뢰불가 경계로 감싼다.
        yield return new ToolOutput(Reminders.UntrustedToolOutput + Render(data));
    }

    // 첫 키워드가 읽기전용 허용 목록이고, 다중문(세미콜론 뒤 내용)이 아니면 true.
    public static bool IsReadOnlySql(string sql)
    {
        var s = StripLeading(sql);
        var firstWord = new string(s.TakeWhile(char.IsLetter).ToArray());
        if (!ReadOnlyStart.Contains(firstWord))
        {
            return false;
        }

        // 다중문 차단: 문자열 리터럴 밖의 세미콜론 뒤에 실질 내용이 있으면 거부.
        var inSingle = false;
        var inDouble = false;
        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '"' && !inSingle) inDouble = !inDouble;
            else if (c == ';' && !inSingle && !inDouble)
            {
                if (sql[(i + 1)..].Any(x => !char.IsWhiteSpace(x)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static string StripLeading(string sql)
    {
        var s = sql.TrimStart();
        // 선행 라인주석(-- ...) 제거 후 다시 트림.
        while (s.StartsWith("--", StringComparison.Ordinal))
        {
            var nl = s.IndexOf('\n');
            s = nl < 0 ? string.Empty : s[(nl + 1)..].TrimStart();
        }

        return s;
    }

    private static string Render(QueryResponse? data)
    {
        if (data is null)
        {
            return L10n.Get("tools.orgDatas.emptyResponse");
        }

        if (data.Columns is { Count: > 0 } cols && data.Rows is not null)
        {
            var sb = new StringBuilder();
            var n = data.RowCount ?? data.Rows.Count;
            sb.Append(L10n.Get("tools.orgDatas.resultHeader", n));
            if (data.Truncated == true)
            {
                sb.Append(L10n.Get("tools.orgDatas.truncated"));
            }

            sb.AppendLine().AppendLine();
            sb.Append("| ").Append(string.Join(" | ", cols)).AppendLine(" |");
            sb.Append("|").Append(string.Join("|", cols.Select(_ => "---"))).AppendLine("|");
            foreach (var row in data.Rows)
            {
                sb.Append("| ")
                  .Append(string.Join(" | ", row.Select(Cell)))
                  .AppendLine(" |");
            }

            return sb.ToString().TrimEnd();
        }

        // 폴백: 서버가 markdown 만 준 경우.
        return string.IsNullOrWhiteSpace(data.MarkdownTable) ? L10n.Get("tools.orgDatas.noResults") : data.MarkdownTable!.Trim();
    }

    private static string Cell(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Null => "",
        _ => v.ToString(),
    };

    private static async Task<QueryResponse?> CallAsync(string url, string key, Input inp, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);

        var payload = new
        {
            collection_id = inp.CollectionId,
            sql = inp.Sql,
            maxRows = inp.MaxRows is { } k ? Math.Clamp(k, 1, 500) : 100,
        };

        using var resp = await client.PostAsJsonAsync(url, payload, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            throw new EndpointMissingException();
        }

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {body.Trim()}");
        }

        return await resp.Content.ReadFromJsonAsync<QueryResponse>(cancellationToken: ct).ConfigureAwait(false);
    }

    private sealed class EndpointMissingException : Exception
    {
    }
}
