using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MoaiCode.Core.Agent;
using MoaiCode.Providers;

namespace MoaiCode.Cli;

/// <summary>
/// 비대화형 1회 실행 (moai run / moai -p). 텍스트는 stdout, 툴 진행은 stderr로 분리해
/// 파이프라인/CI 친화적 출력 제공. outputFormat="json" 이면 답변 대신 단일 JSON(결과+토큰)만 stdout 에.
/// </summary>
public static class HeadlessRunner
{
    // 헤드리스·stream-json 공통: 출력 한도에 걸린 채 (마지막 툴 호출 뒤) 답 없이 끝났는가 → 종료 코드 3.
    internal static bool EndedAtLimitWithoutAnswer(string stopReason, bool answered) =>
        !answered && string.Equals(stopReason, "length", StringComparison.OrdinalIgnoreCase);

    internal const string NoAnswerMessage =
        "the model hit its output token limit without producing an answer (see MOAI_MAX_TOKENS).";

    public static Task<int> RunAsync(QueryEngine engine, string prompt, CancellationToken ct)
        => RunAsync(engine, prompt, ct, "text");

    public static async Task<int> RunAsync(QueryEngine engine, string prompt, CancellationToken ct, string outputFormat)
    {
        var json = string.Equals(outputFormat, "json", StringComparison.OrdinalIgnoreCase);
        var stopReason = "end_turn";

        // 추론 마커(<think>…, __THINKING_STATUS__:…)는 델타 경계에 걸쳐 쪼개져 오므로 조각 단위로는
        // 지울 수 없다. 텍스트 런을 모아 ThinkFilter 를 적용한 뒤 내보낸다.
        // 텍스트 모드: stdout 스트리밍. json 모드: 전체를 모아 마지막에 JSON 으로.
        var run = new StringBuilder();
        var full = new StringBuilder();

        // 마지막 툴 호출 뒤에 답변 텍스트가 나왔는가 — 앞 턴의 예고문("먼저 살펴보겠습니다")은 답이 아니다.
        var answered = false;

        void FlushText()
        {
            if (run.Length == 0)
            {
                return;
            }

            var clean = ThinkFilter.Strip(run.ToString());
            run.Clear();
            if (clean.Length == 0)
            {
                return;
            }

            answered = true;
            if (json)
            {
                if (full.Length > 0) full.Append('\n');
                full.Append(clean);
            }
            else
            {
                Console.Out.WriteLine(clean);
            }
        }

        int? errCode = null;
        string? errMsg = null;
        try
        {
            await foreach (var ev in engine.SubmitAsync(prompt, ct).WithCancellation(ct))
            {
                switch (ev)
                {
                    case TextDelta d:
                        run.Append(d.Text);
                        break;
                    case ToolCallRequested t:
                        FlushText();
                        answered = false;
                        Console.Error.WriteLine($"→ {t.Block.Name}");
                        break;
                    case ToolExecuted x:
                        // 툴 실패는 실행 실패가 아니다 — 모델이 결과를 보고 이어서 고친다(Read 누락·없는 경로 등).
                        // 예전엔 한 번이라도 있으면 종료 코드 1/is_error=true 라, 과제를 끝낸 실행도 실패로 보였다.
                        Console.Error.WriteLine($"{(x.IsError ? "✗" : "✓")} {x.ToolName}");
                        break;
                    case TurnCompleted tc:
                        FlushText();
                        stopReason = tc.StopReason;
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            FlushText(); errCode = 130; errMsg = "execution canceled."; stopReason = "canceled";
        }
        catch (ProviderException ex)
        {
            FlushText(); errCode = 2; errMsg = $"provider error: {ex.Message}"; stopReason = "error";
        }
        catch (Exception ex)
        {
            FlushText(); errCode = 1; errMsg = $"execution error: {ex.Message}"; stopReason = "error";
        }

        FlushText();

        // 출력 한도에 걸린 채 답 없이 끝났으면 실패로 알린다 — 예전엔 종료 코드 0 에 빈 출력이라
        // 호출자(스크립트·브리지)가 성공으로 오인했다.
        if (errCode is null && EndedAtLimitWithoutAnswer(stopReason, answered))
        {
            errCode = 3;
            errMsg = NoAnswerMessage;
        }

        var u = engine.CumulativeUsage;
        var isError = errCode is not null;

        if (json)
        {
            // claude -p --output-format json 과 유사한 스크립트 친화 출력.
            var obj = new JsonObject
            {
                ["result"] = errMsg ?? full.ToString(),
                ["is_error"] = isError,
                ["stop_reason"] = stopReason,
                ["usage"] = new JsonObject
                {
                    ["input_tokens"] = u.InputTokens,
                    ["output_tokens"] = u.OutputTokens,
                    ["cache_read_input_tokens"] = u.CacheReadTokens,
                    ["cache_creation_input_tokens"] = u.CacheCreationTokens,
                },
            };
            Console.Out.WriteLine(obj.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
            return errCode ?? 0;
        }

        if (errMsg is not null)
        {
            Console.Error.WriteLine(errMsg);
        }

        // 진단/벤치마크용 토큰 사용량 출력(opt-in, 텍스트 모드). stdout(답변) 무오염 위해 stderr 로.
        if (Environment.GetEnvironmentVariable("MOAI_PRINT_USAGE") is "1" or "true")
        {
            Console.Error.WriteLine(
                $"__USAGE__ {{\"in\":{u.InputTokens},\"out\":{u.OutputTokens}," +
                $"\"cache_read\":{u.CacheReadTokens},\"cache_creation\":{u.CacheCreationTokens}}}");
        }

        return errCode ?? 0;
    }
}
