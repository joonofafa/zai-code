using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;
using MoaiCode.Tui;

namespace MoaiCode.Cli;

/// <summary>
/// stream-json 영속 모드용 확인 게이트. 확인이 필요한 툴 호출을 stdout 으로 permission_request
/// 이벤트로 내보내고, stdin 의 permission_response 를 기다린다(타임아웃 초과/응답 불가는 거부 — fail-closed).
/// AppBootstrap.ModeAwarePermissionGate 의 confirmer 슬롯에 연결된다.
/// </summary>
public sealed class StreamJsonPermissionGate : IPermissionGate
{
    /// <summary>승인 대기 상한(초). env MOAI_PERMISSION_TIMEOUT 으로 조정. 만료·stdin EOF 는 거부.</summary>
    public static int TimeoutSeconds()
    {
        var v = Environment.GetEnvironmentVariable("MOAI_PERMISSION_TIMEOUT");
        return int.TryParse(v, out var n) && n > 0 ? n : 300;
    }

    private readonly Action<JsonObject> _emit;
    private readonly Action<string>? _persistAllow;

    private sealed record Pending(TaskCompletionSource<bool> Tcs, string? Scope);

    // 진행 중인 승인 요청: request_id → 완료 콜백(+항상허용 스코프). stdin 리더가 response 를 여기로 흘려준다.
    private readonly Dictionary<string, Pending> _pending = new();
    private readonly object _lock = new();

    public StreamJsonPermissionGate(Action<JsonObject>? emit = null, Action<string>? persistAllow = null)
    {
        _emit = emit ?? StreamJsonRunner.Emit;
        _persistAllow = persistAllow;
    }

    public async ValueTask<bool> AllowAsync(ITool tool, ToolUseBlock call, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N")[..12];
        var display = ToolDisplay.Describe(tool.Name, call.Input);
        if (display.Length > 2000)
        {
            display = display[..2000] + L10n.Get("permission.truncated");
        }

        // "항상 허용" 스코프. 못 구하면 버튼에서 제외(스코프 없는 exact 저장은 위험하니 once/deny 만).
        var scope = PermissionRule.TryScope(tool, call);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _pending[requestId] = new Pending(tcs, scope);
        }

        var req = new JsonObject
        {
            ["type"] = "permission_request",
            ["request_id"] = requestId,
            ["tool"] = tool.Name,
            ["display"] = display,
            ["timeout_seconds"] = TimeoutSeconds(),
        };
        if (scope is not null)
        {
            req["scope"] = scope;
        }
        _emit(req);

        // 타임아웃·취소까지 통합: 응답/만료/EOF 중 먼저 도달하는 것. 어느 쪽이든 거부가 기본값(fail-closed).
        // 응답/타임아웃 중 먼저 도달하는 쪽. 어느 쪽이든 결과는 이미 fail-closed(false 기본).
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds()));
        var timeout = Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token);
        try
        {
            var done = await Task.WhenAny(tcs.Task, timeout).ConfigureAwait(false);
            lock (_lock)
            {
                _pending.Remove(requestId);
            }

            if (ReferenceEquals(done, tcs.Task))
            {
                return tcs.Task.Result;   // 승인(true) 또는 명시적 거부(false)
            }

            // 타임아웃·취소 — 만료 사실을 상대에게도 알려 UI 가 정리되게 한다.
            _emit(new JsonObject
            {
                ["type"] = "permission_result",
                ["request_id"] = requestId,
                ["outcome"] = ct.IsCancellationRequested ? "canceled" : "timeout",
            });
            return false;
        }
        finally
        {
            timeoutCts.Cancel();
            lock (_lock)
            {
                _pending.Remove(requestId);
            }
        }
    }

    /// <summary>stdin 리더가 permission_response 한 줄을 전달한다. 알 수 없는 id 는 무시.</summary>
    public void HandleResponse(string requestId, string decision)
    {
        Pending? p;
        lock (_lock)
        {
            if (!_pending.Remove(requestId, out p))
            {
                return;
            }
        }

        switch (decision)
        {
            case "allow":
                p.Tcs.TrySetResult(true);
                break;
            case "allow_always" when p.Scope is not null && _persistAllow is not null:
                try
                {
                    _persistAllow(p.Scope);
                    p.Tcs.TrySetResult(true);
                }
                catch
                {
                    // 규칙 저장에 실패하면 영구 허용으로 간주하지 않는다.
                    p.Tcs.TrySetResult(false);
                }
                break;
            default:
                p.Tcs.TrySetResult(false);
                break;
        }
    }

    /// <summary>모든 대기 중 요청을 거부로 종료(stdin EOF·프로세스 종료 시). 턴이 승인 대기로 영구 멈추지 않게 한다.</summary>
    public void FailAllPending()
    {
        lock (_lock)
        {
            foreach (var p in _pending.Values)
            {
                p.Tcs.TrySetResult(false);
            }

            _pending.Clear();
        }
    }
}
