using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MoaiCode.Core;
using MoaiCode.Core.Web;

namespace MoaiCode.Tools.Web;

/// <summary>
/// z.ai 외 프로바이더의 내장 웹 검색. 대화 모델과 같은 접속 정보로 별도 요청 한 번을 보내, 검색해서 쓴 답과 출처 목록을 돌려준다.
/// OpenAI 는 Responses API 의 web_search 툴, OpenRouter 는 openrouter:web_search 서버 툴, Claude 는 web_search 서버 툴이다.
/// (vLLM 은 검색 기능이 없어 툴을 쓰지 않는다.)
/// </summary>
internal static class ProviderWebSearch
{
    // 답변은 짧게 요약하라고 하지만 추론 모델은 추론에도 토큰을 쓴다 — 넉넉히.
    private const int MaxTokens = 4096;

    public static Task<string> SearchAsync(ProviderProfile p, string query, int limit, CancellationToken ct) => p.Kind switch
    {
        ProviderKind.OpenAI => OpenAiAsync(p, query, limit, ct),
        ProviderKind.OpenRouter => OpenRouterAsync(p, query, limit, ct),
        ProviderKind.Anthropic => ClaudeAsync(p, query, limit, ct),
        _ => throw new NotSupportedException(p.Kind.ToString()),
    };

    private static string Prompt(string query)
        => $"Search the web for: {query}\nReport the relevant findings concisely and cite the sources you used.";

    // POST /responses — output[] 의 message 안 output_text 와 url_citation 주석.
    private static async Task<string> OpenAiAsync(ProviderProfile p, string query, int limit, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = p.Model,
            ["input"] = Prompt(query),
            ["tools"] = new JsonArray { new JsonObject { ["type"] = "web_search" } },
        };
        using var doc = await PostAsync(p, "/responses", body, bearer: true, ct).ConfigureAwait(false);

        var answer = new StringBuilder();
        var sources = new List<Source>();
        foreach (var item in Array(doc.RootElement, "output"))
        {
            if (Str(item, "type") != "message")
            {
                continue;
            }

            foreach (var c in Array(item, "content"))
            {
                if (Str(c, "type") == "output_text")
                {
                    answer.Append(Str(c, "text"));
                    foreach (var a in Array(c, "annotations"))
                    {
                        if (Str(a, "type") == "url_citation")
                        {
                            sources.Add(new Source(Str(a, "title"), Str(a, "url"), ""));
                        }
                    }
                }
            }
        }

        return Render(answer.ToString(), sources, limit);
    }

    // POST /chat/completions + openrouter:web_search 서버 툴 — message.annotations[].url_citation{url,title,content}.
    private static async Task<string> OpenRouterAsync(ProviderProfile p, string query, int limit, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = p.Model,
            ["max_tokens"] = MaxTokens,
            ["tools"] = new JsonArray { new JsonObject { ["type"] = "openrouter:web_search" } },
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = Prompt(query) } },
        };
        using var doc = await PostAsync(p, "/chat/completions", body, bearer: true, ct).ConfigureAwait(false);

        var answer = "";
        var sources = new List<Source>();
        foreach (var choice in Array(doc.RootElement, "choices"))
        {
            if (!choice.TryGetProperty("message", out var msg))
            {
                continue;
            }

            answer = Str(msg, "content");
            foreach (var a in Array(msg, "annotations"))
            {
                var cite = a.TryGetProperty("url_citation", out var nested) ? nested : a;
                sources.Add(new Source(Str(cite, "title"), Str(cite, "url"), Str(cite, "content")));
            }

            break;
        }

        return Render(answer, sources, limit);
    }

    // POST /messages + web_search 서버 툴 — web_search_tool_result 의 결과 목록과 본문 text.
    // 최신 툴 버전(동적 거르기)은 Opus 4.6·Sonnet 4.6 이후만 받으므로, 거부되면 기본 버전으로 다시 보낸다.
    private static async Task<string> ClaudeAsync(ProviderProfile p, string query, int limit, CancellationToken ct)
    {
        JsonDocument doc;
        try
        {
            doc = await PostAsync(p, "/messages", ClaudeBody(p, query, "web_search_20260209"), bearer: false, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            doc = await PostAsync(p, "/messages", ClaudeBody(p, query, "web_search_20250305"), bearer: false, ct).ConfigureAwait(false);
        }

        using (doc)
        {
            var answer = new StringBuilder();
            var sources = new List<Source>();
            foreach (var block in Array(doc.RootElement, "content"))
            {
                switch (Str(block, "type"))
                {
                    case "text":
                        answer.Append(Str(block, "text"));
                        break;
                    case "web_search_tool_result":
                        // 성공이면 결과 목록(배열), 실패면 error_code 를 담은 객체다.
                        if (block.TryGetProperty("content", out var results) && results.ValueKind == JsonValueKind.Object)
                        {
                            throw new HttpRequestException($"web search error: {Str(results, "error_code")}");
                        }

                        foreach (var r in Array(block, "content"))
                        {
                            sources.Add(new Source(Str(r, "title"), Str(r, "url"), Str(r, "page_age")));
                        }

                        break;
                }
            }

            return Render(answer.ToString(), sources, limit);
        }
    }

    private static JsonObject ClaudeBody(ProviderProfile p, string query, string toolType) => new()
    {
        ["model"] = p.Model,
        ["max_tokens"] = MaxTokens,
        ["tools"] = new JsonArray { new JsonObject { ["type"] = toolType, ["name"] = "web_search", ["max_uses"] = 3 } },
        ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = Prompt(query) } },
    };

    private static async Task<JsonDocument> PostAsync(ProviderProfile p, string path, JsonObject body, bool bearer, CancellationToken ct)
    {
        using var client = ToolHttp.CreateApi();
        using var req = new HttpRequestMessage(HttpMethod.Post, p.BaseUrl!.TrimEnd('/') + path)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (bearer)
        {
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {p.ApiKey}");
        }
        else
        {
            req.Headers.TryAddWithoutValidation("x-api-key", p.ApiKey);
            req.Headers.TryAddWithoutValidation("anthropic-version", "2026-10-21");
        }

        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            // 본문에 사유(키/쿼터/모델명)가 담겨 오므로 잘라서 그대로 노출한다.
            throw new HttpRequestException(
                $"HTTP {(int)resp.StatusCode}: {text[..Math.Min(text.Length, 400)]}", null, resp.StatusCode);
        }

        return JsonDocument.Parse(text);
    }

    private sealed record Source(string Title, string Url, string Note);

    // 답 + 출처 목록(중복 URL 제거, limit 개까지). z.ai 결과 목록과 비슷하게 번호·제목·URL·메모 순.
    private static string Render(string answer, List<Source> sources, int limit)
    {
        var sb = new StringBuilder(answer.Trim());
        var unique = sources
            .Where(s => s.Url.Length > 0)
            .DistinctBy(s => s.Url)
            .Take(limit)
            .ToList();
        if (unique.Count > 0)
        {
            if (sb.Length > 0)
            {
                sb.AppendLine().AppendLine();
            }

            sb.AppendLine("Sources:");
            var i = 1;
            foreach (var s in unique)
            {
                sb.Append(i++).Append(". ").AppendLine(s.Title.Length > 0 ? s.Title : s.Url);
                sb.Append("   ").AppendLine(s.Url);
                if (s.Note.Length > 0)
                {
                    var note = s.Note.ReplaceLineEndings(" ").Trim();
                    sb.Append("   ").AppendLine(note.Length <= 300 ? note : note[..300] + "…");
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static IEnumerable<JsonElement> Array(JsonElement o, string name)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray()
            : [];

    private static string Str(JsonElement o, string name)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "").Trim()
            : "";
}
