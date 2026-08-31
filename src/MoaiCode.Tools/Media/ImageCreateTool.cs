using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;

namespace MoaiCode.Tools.Media;

/// <summary>
/// 텍스트 프롬프트로 이미지를 생성해 PNG 로 저장한다.
/// Z.ai API 계약: POST https://api.z.ai/api/paas/v4/images/generations (Bearer).
/// 저장된 PNG 는 DocxCreate/PptxCreate 의 image 필드로 문서에 삽입할 수 있다.
/// </summary>
public sealed class ImageCreateTool : ITool
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    public string Name => "ImageCreate";

    public string Description => """
        Generates an image from a text prompt and saves it as a PNG file.
        Input: prompt (required — what to draw), path (output .png, relative to workspace),
        model (optional — server's default image model is used if omitted).
        Use for illustrations, figures, diagrams, cover art. The saved PNG can be embedded
        into documents via DocxCreate/PptxCreate (their `image`/`images` fields).
        """;

    public bool IsReadOnly => false;
    public bool IsConcurrencySafe => false;

    public JsonElement InputSchema { get; } = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "prompt": { "type": "string", "description": "What to draw (image description)" },
            "path": { "type": "string", "description": "Output .png path (relative to workspace)" },
            "model": { "type": "string", "description": "Image model id (optional; server default if omitted)" }
          },
          "required": ["prompt", "path"]
        }
        """);

    private sealed record Input(
        [property: JsonPropertyName("prompt")] string? Prompt,
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("model")] string? Model);

    private sealed record GenResponse(
        [property: JsonPropertyName("data")] List<Datum>? Data,
        [property: JsonPropertyName("model")] string? Model);

    private sealed record Datum(
        [property: JsonPropertyName("b64_json")] string? B64,
        [property: JsonPropertyName("mime_type")] string? MimeType,
        [property: JsonPropertyName("revised_prompt")] string? RevisedPrompt);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        var inp = input.Deserialize<Input>();
        if (inp is null || string.IsNullOrWhiteSpace(inp.Prompt))
        {
            yield return new ToolOutput("ImageCreate: 'prompt' is required", IsError: true);
            yield break;
        }

        if (string.IsNullOrWhiteSpace(inp.Path))
        {
            yield return new ToolOutput("ImageCreate: 'path' is required", IsError: true);
            yield break;
        }

        var baseUrl = Environment.GetEnvironmentVariable("ZAI_IMAGE_BASE_URL")
                      ?? "https://api.z.ai/api/paas/v4";
        var key = Environment.GetEnvironmentVariable("ZAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            yield return new ToolOutput(
                L10n.Get("tools.imageCreate.notLoggedIn"), IsError: true);
            yield break;
        }

        string outPath = string.Empty;
        string? pathErr = null;
        try
        {
            var p = inp.Path!;
            if (!p.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                p += ".png";
            }

            outPath = Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(context.WorkingDirectory, p));
        }
        catch (Exception ex)
        {
            pathErr = L10n.Get("tools.imageCreate.badPath", ex.Message);
        }

        if (pathErr is not null)
        {
            yield return new ToolOutput(pathErr, IsError: true);
            yield break;
        }

        yield return new ToolStatus(L10n.Get("tools.imageCreate.generating", inp.Prompt!.Trim()));

        var url = baseUrl.TrimEnd('/') + "/images/generations";
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(Timeout);

        string? error = null;
        GenResponse? data = null;
        try
        {
            data = await CallAsync(url, key, inp.Prompt!, inp.Model, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            error = L10n.Get("tools.imageCreate.timeout", Timeout.TotalSeconds);
        }
        catch (HttpRequestException ex)
        {
            error = L10n.Get("tools.imageCreate.requestFailed", ex.Message);
        }
        catch (Exception ex)
        {
            error = L10n.Get("tools.imageCreate.error", ex.Message);
        }

        if (error is not null)
        {
            yield return new ToolOutput(error, IsError: true);
            yield break;
        }

        var b64 = data?.Data is { Count: > 0 } ? data.Data[0].B64 : null;
        if (string.IsNullOrEmpty(b64))
        {
            yield return new ToolOutput(L10n.Get("tools.imageCreate.noImage"), IsError: true);
            yield break;
        }

        string? writeErr = null;
        try
        {
            var bytes = Convert.FromBase64String(b64);
            var dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllBytesAsync(outPath, bytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            writeErr = L10n.Get("tools.imageCreate.saveFailed", ex.Message);
        }

        if (writeErr is not null)
        {
            yield return new ToolOutput(writeErr, IsError: true);
            yield break;
        }

        var kb = new FileInfo(outPath).Length / 1024.0;
        yield return new ToolOutput(L10n.Get("tools.imageCreate.created", outPath, kb, data?.Model));
    }

    private static async Task<GenResponse?> CallAsync(
        string url, string key, string prompt, string? model, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

        object payload = string.IsNullOrWhiteSpace(model)
            ? new { prompt, response_format = "b64_json" }
            : new { prompt, model, response_format = "b64_json" };

        using var resp = await client.PostAsJsonAsync(url, payload, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {body.Trim()}");
        }

        return await resp.Content.ReadFromJsonAsync<GenResponse>(cancellationToken: ct).ConfigureAwait(false);
    }
}
