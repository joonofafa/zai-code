using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using MoaiCode.Core.Agent.Prompts;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;

namespace MoaiCode.Core.Agent;

/// <summary>
/// 상태를 가진 대화 엔진 (TS QueryEngine 대응 — 메시지 스토어 소유).
/// 모델 스트리밍 → 권한 게이트 → 툴 디스패치 → 결과 주입 → 재진입(멀티턴).
/// 복구 분기(reactive compact / provider fallback 등)는 후속 상태머신화.
/// 설계 근거: ../../CSHARP_PORT_PLAN.md 4.1.
/// </summary>
public sealed class QueryEngine
{
    private readonly IChatModel _model;
    private readonly IReadOnlyList<ITool> _tools;
    private readonly IPermissionGate _gate;
    private readonly IToolObserver _observer;
    private readonly int _maxTurns;
    private readonly string _workingDirectory;

    // 컨텍스트 관리: 누적 토큰이 이 선을 넘으면 선제 컴팩션 (창의 약 70%).
    private readonly int _compactTokens;
    private const int MaxContextRecoveries = 3;

    // 단일 툴 결과가 컨텍스트에 들어갈 때의 문자 상한. 거대한 grep/bash/read 출력이 창을 폭주시켜
    // 잦은(손실 있는) 컴팩션을 유발하는 것을 막는다. <=0 이면 무제한. UI 표시는 원문 그대로.
    private readonly int _maxToolResultChars;

    // max_turns 도달 시 곧장 멈추지 않고, 압축 후 턴을 연장할 수 있는 최대 횟수.
    private const int MaxTurnExtensions = 3;

    // max_turns 도달 시 압축 후 연장할지. 메인 에이전트는 true(긴 작업 지속),
    // 서브에이전트는 false(빠른 보조 — 무한정 길어지면 안 됨).
    private readonly bool _extendTurns;

    private readonly List<Message> _seed = new();
    private readonly List<Message> _messages = new();
    private readonly ReadTracker _reads = new();

    // 이번 SubmitAsync 의 원래 사용자 요청 — 컴팩션/연장 후 표류 방지를 위해 다시 고정(re-anchor).
    private string _goal = string.Empty;

    // 미완료 작업이 남아있는지(task list). 모델이 일을 남긴 채 종료하려 하면 완료를 독려하는 데 사용.
    private readonly Func<bool>? _pendingTasks;

    public QueryEngine(
        IChatModel model,
        IReadOnlyList<ITool> tools,
        IPermissionGate? gate = null,
        IToolObserver? observer = null,
        int maxTurns = 12,
        string? workingDirectory = null,
        int contextWindowTokens = 200_000,
        bool extendTurns = true,
        Func<bool>? pendingTasks = null,
        int maxToolResultChars = 16_000)
    {
        _model = model;
        _tools = tools;
        _gate = gate ?? new AutoApproveGate();
        _observer = observer ?? NullToolObserver.Instance;
        _maxTurns = maxTurns;
        _workingDirectory = workingDirectory ?? Directory.GetCurrentDirectory();
        _compactTokens = Math.Max(1_000, contextWindowTokens * 70 / 100);
        _extendTurns = extendTurns;
        _pendingTasks = pendingTasks;
        _maxToolResultChars = maxToolResultChars;
    }

    public IReadOnlyList<Message> Messages => _messages;

    /// <summary>세션 누적 토큰 사용량 (/cost 표시용).</summary>
    public Usage CumulativeUsage { get; private set; } = new(0, 0);

    /// <summary>초기 메시지(시스템 프롬프트 등) 설정. /clear 시 이 상태로 복원.</summary>
    public void Seed(IEnumerable<Message> initial)
    {
        _seed.Clear();
        _seed.AddRange(initial);
        Reset();
    }

    public void Reset()
    {
        _messages.Clear();
        _messages.AddRange(_seed);
    }

    /// <summary>저장된 세션 메시지로 대화 상태를 교체 (/resume).</summary>
    public void Restore(IReadOnlyList<Message> messages)
    {
        _messages.Clear();
        _messages.AddRange(messages);

        // 복원된 대화에서 이미 Read/Write/Edit 한 파일 경로를 ReadTracker 에 재등록한다.
        // ReadTracker 는 메모리 상태라 /resume 후 비어 있어, 모델은 "이미 읽었다"고 믿는데 write 전
        // read 가드가 막아 Write 를 무한 재시도하는 루프가 생긴다 — 그걸 방지.
        foreach (var m in messages)
        {
            if (m is not AssistantMessage a)
            {
                continue;
            }

            foreach (var tu in a.Content.OfType<ToolUseBlock>())
            {
                if (tu.Name is not ("Read" or "Write" or "Edit"))
                {
                    continue;
                }

                if (tu.Input.ValueKind == JsonValueKind.Object
                    && tu.Input.TryGetProperty("path", out var pe)
                    && pe.ValueKind == JsonValueKind.String
                    && pe.GetString() is { Length: > 0 } path)
                {
                    var full = Path.IsPathRooted(path) ? path : Path.Combine(_workingDirectory, path);
                    _reads.MarkRead(full);
                }
            }
        }
    }

    /// <summary>
    /// 대화에 시스템 리마인더(사용자 역할로 래핑)를 주입. 모드 전환 등 상태 변화를
    /// 모델에 알리는 용도. 다음 SubmitAsync 호출 시 모델이 보게 된다.
    /// </summary>
    public void AddSystemReminder(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            _messages.Add(new UserMessage($"<system-reminder>\n{text.Trim()}\n</system-reminder>"));
        }
    }

    public async IAsyncEnumerable<StreamEvent> SubmitAsync(
        string userInput,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _messages.Add(new UserMessage(userInput));
        _goal = userInput;

        var toolContext = new ToolContext(_workingDirectory, PermissionMode.Auto, _reads);
        var failureCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var successCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var dupNudged = new HashSet<string>(StringComparer.Ordinal);
        var continuationNudges = 0;
        var outputRecoveries = 0;
        var toolCallRetries = 0;
        var announceNudges = 0;
        var goalNudges = 0;
        var turn = 0;
        var extensions = 0;

        while (true)
        {
            // 세그먼트(maxTurns)를 소진하면, 상한 내에서 컨텍스트를 압축하고 턴을 연장한다.
            // 그냥 멈추지 않고 긴 작업을 이어가되, 무한 루프는 MaxTurnExtensions로 막는다.
            if (turn >= _maxTurns)
            {
                if (!_extendTurns || extensions >= MaxTurnExtensions)
                {
                    // 빈손으로 멈추지 않는다: 툴을 끄고 마지막 답변을 한 번 받아 결과를 돌려준다.
                    // (그냥 max_turns 로 끊으면 토큰만 쓰고 답이 없다.)
                    await foreach (var ev in StreamFinalAnswerAsync(ct).ConfigureAwait(false))
                    {
                        yield return ev;
                    }

                    yield break;
                }

                extensions++;
                turn = 0;
                await ForceCompactAsync(ct).ConfigureAwait(false);
                _messages.Add(new UserMessage(Reminders.MaxTurnsExtended));
                // 압축으로 희석된 원래 의도를 다시 고정 — 검증/탐색으로 표류하지 않게.
                var anchor = GoalReminder();
                if (anchor.Length > 0)
                {
                    _messages.Add(new UserMessage(anchor));
                }
            }

            turn++;

            // 선제 컴팩션이 실제로 일어났으면, 요약에 묻힌 원래 의도를 다시 고정한다(표류 방지).
            if (await MaybeCompactAsync(ct).ConfigureAwait(false))
            {
                var ga = GoalReminder();
                if (ga.Length > 0)
                {
                    _messages.Add(new UserMessage(ga));
                }
            }

            var assistantText = new StringBuilder();
            var toolCalls = new List<ToolUseBlock>();
            var stopReason = "end_turn";
            Usage lastUsage = new(0, 0);

            // 모델에 보내기 전 tool_use ↔ tool_result 짝을 보증한다. 실패-루프 가드가 툴 처리 도중
            // 턴을 끊거나, 손상된 세션을 /resume 하면 tool_result 없는 tool_use(고아)가 남아 Anthropic 등
            // 엄격한 API 가 400을 낸다("tool_use ids were found without tool_result blocks").
            EnsureToolResultsPaired();

            // 모델 스트림을 '시작'할 때 컨텍스트 초과(400)면 응급 컴팩션 후 재시도한다.
            // iterator 메서드는 try/catch 안에서 yield 할 수 없으므로, 예외가 나는 지점(첫 MoveNext)만
            // try로 감싸고 — 컨텍스트 초과 400은 토큰이 흘러나오기 전에 즉시 떨어진다 — 소비는 그 밖에서 한다.
            var stream = _model.StreamAsync(_messages, _tools, ct).GetAsyncEnumerator(ct);
            var hasNext = false;
            for (var attempt = 0; ; attempt++)
            {
                IModelException? overflow = null;
                try
                {
                    hasNext = await stream.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (
                    ex is IModelException { IsContextOverflow: true } m && attempt < MaxContextRecoveries)
                {
                    overflow = m;
                }

                if (overflow is null)
                {
                    break; // 정상 시작 (또는 컨텍스트 초과가 아닌 예외 → 그대로 전파됨)
                }

                await stream.DisposeAsync().ConfigureAwait(false);
                if (!await ForceCompactAsync(ct).ConfigureAwait(false))
                {
                    throw (Exception)overflow; // 더 줄일 게 없으면 원래 오류를 사용자에게 노출
                }

                stream = _model.StreamAsync(_messages, _tools, ct).GetAsyncEnumerator(ct);
            }

            try
            {
                while (hasNext)
                {
                    var ev = stream.Current;
                    switch (ev)
                    {
                        case TextDelta d:
                            assistantText.Append(d.Text);
                            yield return ev;
                            break;
                        case ToolCallRequested t:
                            toolCalls.Add(t.Block);
                            yield return ev;
                            break;
                        case TurnCompleted c:
                            stopReason = c.StopReason;
                            lastUsage = c.Usage;
                            break;
                    }

                    hasNext = await stream.MoveNextAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            CumulativeUsage = AddUsage(CumulativeUsage, lastUsage);

            // 모델이 content 에 섞은 추론(<think>...</think>)은 저장 전에 제거 (히스토리/세션/컴팩션 오염 방지).
            var cleanText = ThinkFilter.Strip(assistantText.ToString());

            var blocks = new List<ContentBlock>();
            if (cleanText.Length > 0)
            {
                blocks.Add(new TextBlock(cleanText));
            }

            blocks.AddRange(toolCalls);
            if (blocks.Count > 0)
            {
                _messages.Add(new AssistantMessage(blocks));
            }

            if (toolCalls.Count == 0)
            {
                // 출력 토큰 한도로 잘렸으면(stop=length) 이어받기 유도 후 재개 (상한 내).
                if (IsOutputTruncated(stopReason) && outputRecoveries < 3)
                {
                    outputRecoveries++;
                    _messages.Add(new UserMessage(Reminders.OutputLimitRecovery));
                    continue;
                }

                // 모델이 tool_calls 로 턴을 끝냈는데 파싱된 툴콜이 0개 → 누락/글리치.
                // 이어서 진행하도록 재요청 (상한). 이대로 종료하면 작업이 중간에 멈춘다.
                if (string.Equals(stopReason, "tool_calls", StringComparison.OrdinalIgnoreCase)
                    && toolCallRetries < 2)
                {
                    toolCallRetries++;
                    _messages.Add(new UserMessage(Reminders.MissingToolCall));
                    continue;
                }

                // 액션을 말로만 예고하고("~하겠습니다"/"I'll …") 툴은 안 부른 채 정상 종료한 경우:
                // 실제로 하라고 1회 재촉 (루프 방지로 1회 상한).
                if (announceNudges < 1
                    && IsNormalStop(stopReason)
                    && LooksLikeAnnouncedAction(cleanText))
                {
                    announceNudges++;
                    _messages.Add(new UserMessage(Reminders.MissingToolCall));
                    continue;
                }

                // 빈 응답(추론 제거 후 텍스트도 툴호출도 없음)이면 한 번 더 진행을 유도 (최대 2회).
                if (cleanText.Length == 0 && continuationNudges < 2)
                {
                    continuationNudges++;
                    _messages.Add(new UserMessage(Reminders.ContinuationNudge));
                    continue;
                }

                // 미완료 task 를 남긴 채 종료하려 하면 완료를 독려 (상한 2회). 자율 루프가 아니라,
                // 모델이 만든 task list 가 끝나지 않았을 때만 한 번 더 진행을 유도하는 안전한 넛지.
                if (_pendingTasks?.Invoke() == true && goalNudges < 2 && IsNormalStop(stopReason))
                {
                    goalNudges++;
                    _messages.Add(new UserMessage(Reminders.UnfinishedTasks));
                    var ga = GoalReminder();
                    if (ga.Length > 0)
                    {
                        _messages.Add(new UserMessage(ga));
                    }

                    continue;
                }

                yield return new TurnCompleted(lastUsage, stopReason);
                yield break;
            }

            // 관찰 메시지는 한 턴의 모든 tool_result를 추가한 뒤에야 주입한다.
            // (어시스턴트의 tool_calls에 대한 tool_result들 사이에 user 메시지가 끼면
            //  OpenAI 호환 API에서 tool_use/tool_result 쌍이 깨져 400이 날 수 있다.)
            var pendingObservations = new List<string>();

            foreach (var call in toolCalls)
            {
                var tool = _tools.FirstOrDefault(t =>
                    string.Equals(t.Name, call.Name, StringComparison.Ordinal));

                if (tool is null)
                {
                    var msg = $"Unknown tool: {call.Name}";
                    _messages.Add(new ToolResultMessage(call.Id, msg, true));
                    yield return new ToolExecuted(call.Name, call.Id, msg, true);
                    continue;
                }

                if (!tool.IsReadOnly && !await _gate.AllowAsync(tool, call, ct).ConfigureAwait(false))
                {
                    _messages.Add(new ToolResultMessage(call.Id, Reminders.PermissionDenied, true));
                    yield return new ToolExecuted(call.Name, call.Id, Reminders.PermissionDenied, true);
                    continue;
                }

                await _observer.BeforeToolAsync(tool, call, toolContext, ct).ConfigureAwait(false);
                var (output, isError) = await ExecuteToolAsync(tool, call, toolContext, ct).ConfigureAwait(false);
                // 컨텍스트(모델)로 가는 결과만 상한을 건다 — UI(ToolExecuted)와 관찰자엔 원문 유지.
                _messages.Add(new ToolResultMessage(call.Id, CapToolOutput(output, _maxToolResultChars), isError));
                yield return new ToolExecuted(call.Name, call.Id, output, isError);

                var observation = await _observer
                    .AfterToolAsync(tool, call, toolContext, output, isError, ct)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(observation))
                {
                    pendingObservations.Add(observation!);
                }

                // 동일 호출(툴+인자)이 연속 3회 실패하면 루프로 보고 멈춘다 (toolFailureLoopGuard).
                // 키를 툴 이름이 아니라 인자까지 포함해야, 서로 다른 경로/패턴을 탐색하다 몇 번 실패한 것을
                // "루프"로 오인해 세션을 끊지 않는다 (진짜 루프 = 같은 호출 반복).
                var sig = call.Name + " " + call.Input.GetRawText();
                if (isError)
                {
                    var n = failureCounts.TryGetValue(sig, out var c) ? c + 1 : 1;
                    failureCounts[sig] = n;
                    if (n >= 3)
                    {
                        foreach (var obs in pendingObservations)
                        {
                            _messages.Add(new UserMessage(obs));
                        }

                        var stop = Reminders.ToolFailureLoop(call.Name, n);
                        _messages.Add(new UserMessage(stop));
                        yield return new TurnCompleted(lastUsage, "tool_failure_loop");
                        yield break;
                    }
                }
                else
                {
                    failureCounts[sig] = 0;

                    // 성공한 '동일' 읽기 호출(Grep/Glob/Read 등)을 반복하면 한 번만 부드럽게 막는다.
                    // (이미 결과가 위에 있는데 같은 호출을 또 하는 제자리걸음 방지.)
                    // 주의: 여기서 _messages 에 직접 user 메시지를 넣으면 tool_result 들 사이에 끼어들어
                    // tool_use/tool_result 연속성이 깨진다(Anthropic 400). 관찰과 함께 '전부 뒤'로 미룬다.
                    var sc = successCounts.TryGetValue(sig, out var s) ? s + 1 : 1;
                    successCounts[sig] = sc;
                    if (sc >= 2 && tool.IsReadOnly && dupNudged.Add(sig))
                    {
                        pendingObservations.Add(Reminders.DuplicateToolCall(call.Name));
                    }
                }
            }

            // 모든 tool_result가 추가된 뒤 per-tool 관찰 + 턴 종료 검증(lint/test 디바운스)을 주입.
            foreach (var obs in pendingObservations)
            {
                _messages.Add(new UserMessage(obs));
            }

            var turnObservation = await _observer.AfterTurnAsync(toolContext, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(turnObservation))
            {
                _messages.Add(new UserMessage(turnObservation!));
            }
        }
    }

    // 모델 전송 전 tool_use ↔ tool_result 짝을 '양방향'으로 정규화한다(Anthropic 등 엄격 API 대응).
    // 규칙: assistant 의 각 tool_use 바로 뒤에, 같은 순서로, 대응 tool_result 만 연속 배치되어야 한다.
    //  - 결과 없는 tool_use → 합성 결과 삽입   (400: "tool_use ids ... without tool_result")
    //  - 대응 tool_use 없는/중복 tool_result → 제거 (400: "unexpected tool_use_id ... in tool_result")
    //  - tool_result 사이에 끼어든 user 메시지(리마인더 등) → 전부 tool_result 뒤로 재배치
    // 실패-루프 중단·손상된 /resume 세션도 이 단계에서 자가치유된다.
    private void EnsureToolResultsPaired()
    {
        var fixedUp = new List<Message>(_messages.Count);

        for (var i = 0; i < _messages.Count; i++)
        {
            var m = _messages[i];

            if (m is AssistantMessage a && a.Content.OfType<ToolUseBlock>().Any())
            {
                fixedUp.Add(a);
                var ids = a.Content.OfType<ToolUseBlock>().Select(t => t.Id).ToList();
                var wanted = new HashSet<string>(ids, StringComparer.Ordinal);

                // 다음 assistant 전까지의 구간에서 tool_result 를 모으고, 그 외(user 리마인더 등)는 뒤로 미룬다.
                var j = i + 1;
                var found = new Dictionary<string, ToolResultMessage>(StringComparer.Ordinal);
                var deferred = new List<Message>();
                while (j < _messages.Count && _messages[j] is not AssistantMessage)
                {
                    if (_messages[j] is ToolResultMessage tr)
                    {
                        if (wanted.Contains(tr.ToolUseId))
                        {
                            found.TryAdd(tr.ToolUseId, tr);   // 첫 번째만 채택 (중복은 버림)
                        }

                        // 대응 tool_use 가 없는 고아 tool_result 는 버린다.
                    }
                    else
                    {
                        deferred.Add(_messages[j]);
                    }

                    j++;
                }

                // tool_use 순서대로 결과를 '바로 뒤'에 연속 배치(없으면 합성).
                foreach (var id in ids)
                {
                    fixedUp.Add(found.TryGetValue(id, out var tr)
                        ? tr
                        : new ToolResultMessage(id, "Tool call was interrupted; no result was produced.", true));
                }

                fixedUp.AddRange(deferred);   // 끼어 있던 user/리마인더는 tool_result 뒤로
                i = j - 1;
                continue;
            }

            if (m is ToolResultMessage)
            {
                continue;   // 앞에 대응 assistant tool_use 가 없는 고아 tool_result → 버림
            }

            fixedUp.Add(m);
        }

        _messages.Clear();
        _messages.AddRange(fixedUp);
    }

    // 원래 사용자 요청을 다시 고정하는 system-reminder. 컴팩션/연장으로 의도가 희석되는 것을 막아
    // "일단 다 검증/탐색" 으로 표류하지 않게 한다. 너무 길면 잘라낸다.
    private string GoalReminder()
    {
        var g = _goal.Trim();
        if (g.Length == 0)
        {
            return string.Empty;
        }

        if (g.Length > 600)
        {
            g = g[..600] + "…";
        }

        return "<system-reminder>\n" +
               "Stay focused on the user's ORIGINAL request below. Do exactly what it asks and then conclude. " +
               "Do not drift into unrelated verification, re-reading, or exploration beyond what the request needs.\n\n" +
               "Original request: " + g + "\n" +
               "</system-reminder>";
    }

    /// <summary>
    /// max_turns 를 끝내 소진했을 때의 강제 마무리: 툴을 빼고 모델을 한 번 더 호출해
    /// 지금까지 모은 정보로 최종 답을 받아낸다. 빈손(0 tok · stop=max_turns) 종료를 방지.
    /// </summary>
    private async IAsyncEnumerable<StreamEvent> StreamFinalAnswerAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        // 마지막 호출 전 공간 확보 (컨텍스트 초과로 마무리조차 못 하는 일을 줄임).
        await ForceCompactAsync(ct).ConfigureAwait(false);
        var anchor = GoalReminder();
        if (anchor.Length > 0)
        {
            _messages.Add(new UserMessage(anchor));
        }

        _messages.Add(new UserMessage(Reminders.MaxTurnsFinalAnswer));

        var assistantText = new StringBuilder();
        Usage lastUsage = new(0, 0);

        EnsureToolResultsPaired(); // 손상된 짝(고아 tool_use)이 있어도 마무리 호출이 400 나지 않게.

        // 툴 미제공 → 모델은 텍스트로만 마무리한다.
        await foreach (var ev in _model.StreamAsync(_messages, Array.Empty<ITool>(), ct)
                           .WithCancellation(ct).ConfigureAwait(false))
        {
            switch (ev)
            {
                case TextDelta d:
                    assistantText.Append(d.Text);
                    yield return ev;
                    break;
                case TurnCompleted c:
                    lastUsage = c.Usage;
                    break;
            }
        }

        var cleanText = ThinkFilter.Strip(assistantText.ToString());
        if (cleanText.Length > 0)
        {
            _messages.Add(new AssistantMessage(new List<ContentBlock> { new TextBlock(cleanText) }));
        }

        CumulativeUsage = AddUsage(CumulativeUsage, lastUsage);
        yield return new TurnCompleted(lastUsage, "max_turns");
    }

    /// <summary>
    /// 선제 컴팩션: 누적 토큰 추정치가 임계선(창의 ~70%)을 넘으면 오래된 구간을 요약으로 대체.
    /// 메시지 개수가 아니라 토큰을 보므로 거대한 단일 메시지 하나도 트리거된다.
    /// </summary>
    private async Task<bool> MaybeCompactAsync(CancellationToken ct)
    {
        if (EstimateTokens(_messages) < _compactTokens)
        {
            return false;
        }

        return await CompactCoreAsync(12, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 반응형 컴팩션: provider가 컨텍스트 초과를 던졌을 때의 최후 복구.
    /// 보존 구간을 점점 줄여가며 한 번이라도 실제로 줄이면 true.
    /// </summary>
    private async Task<bool> ForceCompactAsync(CancellationToken ct)
    {
        foreach (var keepRecent in new[] { 8, 4, 2 })
        {
            if (await CompactCoreAsync(keepRecent, ct).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 컴팩션 본체 (contextCollapse 이식). 안전 경계: seed(시스템) 앞쪽과 최근 keepRecent개는
    /// 보존하고, 잘리는 꼬리는 반드시 UserMessage에서 시작하도록 해 tool_use/tool_result 쌍이
    /// 쪼개지지 않게 한다. 실제로 요약·치환했으면 true.
    /// </summary>
    private async Task<bool> CompactCoreAsync(int keepRecent, CancellationToken ct)
    {
        var keepFront = _seed.Count;
        var tailStart = Math.Max(keepFront, _messages.Count - keepRecent);
        while (tailStart < _messages.Count && _messages[tailStart] is not UserMessage)
        {
            tailStart++;
        }

        // 요약할 구간이 너무 작으면 스킵.
        if (tailStart - keepFront < 4)
        {
            return false;
        }

        var span = _messages.GetRange(keepFront, tailStart - keepFront);
        string summary;
        try
        {
            summary = await SummarizeSpanAsync(span, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false; // 요약 실패 시 컴팩션 생략 (대화 보존)
        }

        if (string.IsNullOrWhiteSpace(summary))
        {
            return false;
        }

        var rebuilt = new List<Message>(_messages.Count);
        rebuilt.AddRange(_messages.GetRange(0, keepFront));
        rebuilt.Add(new UserMessage("[Summary of earlier conversation]\n" + summary.Trim()));
        rebuilt.AddRange(_messages.GetRange(tailStart, _messages.Count - tailStart));
        _messages.Clear();
        _messages.AddRange(rebuilt);
        return true;
    }

    /// <summary>
    /// 컨텍스트로 들어가는 툴 결과를 상한 이내로 자른다. 넘치면 앞(70%)+뒤(30%)를 남기고 가운데를
    /// 생략 표식으로 대체한다(오류는 흔히 끝에 있으므로 꼬리 보존). maxChars &lt;= 0 이면 원문 그대로.
    /// </summary>
    public static string CapToolOutput(string text, int maxChars)
    {
        if (maxChars <= 0 || text.Length <= maxChars)
        {
            return text;
        }

        var head = maxChars * 7 / 10;
        var tail = maxChars - head;
        var omitted = text.Length - head - tail;
        return text[..head]
            + $"\n\n… [tool output truncated to protect context: {omitted} of {text.Length} chars omitted. "
            + "Narrow the query/range, or write large output to a file and read a summary.] …\n\n"
            + text[^tail..];
    }

    /// <summary>대략적 토큰 추정 (chars/4). 임계선 근처에서만 정확하면 되므로 휴리스틱으로 충분.</summary>
    private static int EstimateTokens(IReadOnlyList<Message> messages)
    {
        long chars = 0;
        foreach (var m in messages)
        {
            switch (m)
            {
                case UserMessage u:
                    chars += u.Text.Length;
                    break;
                case SystemMessage s:
                    chars += s.Text.Length;
                    break;
                case ToolResultMessage tr:
                    chars += tr.Output.Length;
                    break;
                case AssistantMessage a:
                    foreach (var b in a.Content)
                    {
                        if (b is TextBlock t)
                        {
                            chars += t.Text.Length;
                        }
                        else if (b is ToolUseBlock tu)
                        {
                            chars += tu.Name.Length + tu.Input.GetRawText().Length;
                        }
                    }

                    break;
            }
        }

        return (int)(chars / 4);
    }

    private async Task<string> SummarizeSpanAsync(IReadOnlyList<Message> span, CancellationToken ct)
    {
        var rendered = RenderSpan(span);
        var oneShot = new List<Message>
        {
            new SystemMessage(Prompts.CompactionPrompts.BaseCompact),
            new UserMessage(rendered),
        };

        var sb = new StringBuilder();
        await foreach (var ev in _model.StreamAsync(oneShot, Array.Empty<ITool>(), ct).WithCancellation(ct))
        {
            if (ev is TextDelta d)
            {
                sb.Append(d.Text);
            }
        }

        return ExtractSummary(sb.ToString());
    }

    // BASE_COMPACT 출력에서 &lt;summary&gt; 블록만 보존 (&lt;analysis&gt; 작업용 블록은 버림).
    private static string ExtractSummary(string text)
    {
        const string openTag = "<summary>";
        const string closeTag = "</summary>";
        var start = text.IndexOf(openTag, StringComparison.OrdinalIgnoreCase);
        var end = text.IndexOf(closeTag, StringComparison.OrdinalIgnoreCase);
        if (start >= 0 && end > start)
        {
            return text[(start + openTag.Length)..end].Trim();
        }

        // 폴백: <analysis>..</analysis> 만 제거.
        var aStart = text.IndexOf("<analysis>", StringComparison.OrdinalIgnoreCase);
        var aEnd = text.IndexOf("</analysis>", StringComparison.OrdinalIgnoreCase);
        if (aStart >= 0 && aEnd > aStart)
        {
            return (text[..aStart] + text[(aEnd + "</analysis>".Length)..]).Trim();
        }

        return text.Trim();
    }

    private static string RenderSpan(IReadOnlyList<Message> span)
    {
        var sb = new StringBuilder();
        foreach (var m in span)
        {
            switch (m)
            {
                case UserMessage u:
                    sb.Append("User: ").AppendLine(u.Text);
                    break;
                case AssistantMessage a:
                    var text = string.Concat(a.Content.OfType<TextBlock>().Select(t => t.Text));
                    if (text.Length > 0)
                    {
                        sb.Append("Assistant: ").AppendLine(text);
                    }

                    foreach (var tu in a.Content.OfType<ToolUseBlock>())
                    {
                        sb.Append("Assistant tool call: ").AppendLine(tu.Name);
                    }

                    break;
                case ToolResultMessage tr:
                    var preview = tr.Output.Length > 500 ? tr.Output[..500] + "…" : tr.Output;
                    sb.Append("Tool result: ").AppendLine(preview);
                    break;
                case SystemMessage s:
                    sb.Append("System: ").AppendLine(s.Text);
                    break;
            }
        }

        return sb.ToString();
    }

    private static bool IsNormalStop(string stopReason) =>
        string.Equals(stopReason, "stop", StringComparison.OrdinalIgnoreCase)
        || string.Equals(stopReason, "end_turn", StringComparison.OrdinalIgnoreCase);

    // "말로만 예고하고 멈춘" 응답 추정 — 끝이 한국어 의도형(~겠습니다/할게요)이거나
    // 끝부분에 영어 의도(I'll/Let me/I will) 가 있는 경우. (정밀하게: 텍스트 '끝' 기준)
    private static bool LooksLikeAnnouncedAction(string text)
    {
        var t = text.TrimEnd().TrimEnd('.', '!', '?', '。', '~', ' ', '\n', '\r', '"', ')', ']');
        if (t.Length == 0)
        {
            return false;
        }

        // 의도형 어미(~겠습니다/할게요): 모든 "~하겠습니다" 류 포함.
        if (t.EndsWith("겠습니다", StringComparison.Ordinal)
            || t.EndsWith("겠다", StringComparison.Ordinal)
            || t.EndsWith("할게요", StringComparison.Ordinal)
            || t.EndsWith("할게", StringComparison.Ordinal)
            || t.EndsWith("보겠어요", StringComparison.Ordinal))
        {
            return true;
        }

        // 현재형 선언 어미(~합니다/~것입니다): 일반 설명과 헷갈리지 않게 '행동 동사'에 한정.
        // 예: "Redraw 함수를 수정합니다", "...구현합니다", "...추가합니다", "...것입니다".
        string[] actionEndings =
        {
            "수정합니다", "고칩니다", "구현합니다", "추가합니다", "제거합니다", "삭제합니다",
            "변경합니다", "작성합니다", "생성합니다", "적용합니다", "진행합니다", "실행합니다",
            "교체합니다", "만듭니다", "정리합니다", "수정할게요", "것입니다", "하겠음", "할 것입니다",
        };
        foreach (var e in actionEndings)
        {
            if (t.EndsWith(e, StringComparison.Ordinal))
            {
                return true;
            }
        }

        // 영어: 마지막 ~60자에 의도 표현.
        var tail = t.Length <= 60 ? t : t[^60..];
        var lower = tail.ToLowerInvariant();
        return lower.Contains("i'll ") || lower.Contains("i will ")
            || lower.Contains("let me ") || lower.Contains("let's ")
            || lower.Contains("i'm going to ") || lower.Contains("going to ");
    }

    // 출력이 토큰 한도로 잘렸는지 (OpenAI: length, Anthropic: max_tokens).
    private static bool IsOutputTruncated(string stopReason) =>
        string.Equals(stopReason, "length", StringComparison.OrdinalIgnoreCase)
        || string.Equals(stopReason, "max_tokens", StringComparison.OrdinalIgnoreCase);

    private static Usage AddUsage(Usage a, Usage b) => new(
        a.InputTokens + b.InputTokens,
        a.OutputTokens + b.OutputTokens,
        a.CacheReadTokens + b.CacheReadTokens,
        a.CacheCreationTokens + b.CacheCreationTokens);

    private static async Task<(string Output, bool IsError)> ExecuteToolAsync(
        ITool tool, ToolUseBlock call, ToolContext context, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var isError = false;
        try
        {
            await foreach (var progress in tool.ExecuteAsync(call.Input, context, ct).WithCancellation(ct))
            {
                switch (progress)
                {
                    case ToolOutput o:
                        sb.Append(o.Text);
                        if (o.IsError)
                        {
                            isError = true;
                        }

                        break;
                    case ToolStatus:
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ($"Tool '{call.Name}' failed: {ex.Message}", true);
        }

        return (sb.ToString(), isError);
    }
}
