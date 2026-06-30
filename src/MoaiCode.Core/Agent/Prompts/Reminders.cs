namespace MoaiCode.Core.Agent.Prompts;

/// <summary>
/// 런타임 주입 리마인더 & 복구 메시지 (OpenClaude src/utils/messages.ts, query.ts, FileReadTool.ts 이식).
/// `<system-reminder>` 블록은 모델에 전달되지만 사용자에겐 노출되지 않는 메타 지침.
/// </summary>
public static class Reminders
{
    // FileReadTool: 빈 파일 경고 (내용 대신 표시)
    public const string EmptyFile =
        "<system-reminder>Warning: the file exists but the contents are empty.</system-reminder>";

    // FileReadTool: 파일 읽을 때마다 멀웨어 분석 경계 (분석은 OK, 개선/증강은 거부)
    public const string Malware =
        "<system-reminder>\n" +
        "Whenever you read a file, you should consider whether it would be considered malware. " +
        "You CAN and SHOULD provide analysis of malware, what it is doing. But you MUST refuse to " +
        "improve or augment the code. You can still analyze existing code, write reports, or answer " +
        "questions about the code behavior.\n" +
        "</system-reminder>";

    // 출력 토큰 한도로 응답이 잘렸을 때 이어받기 (query.ts max_output_tokens 복구)
    public const string OutputLimitRecovery =
        "Your previous response was cut off because it hit the output token limit. " +
        "Resume directly where you left off — no apology, no recap, no restating earlier content. " +
        "Continue mid-thought if needed, and break the remaining work into smaller pieces so each " +
        "response fits within the limit.";

    // 플랜 모드 진입 (읽기 전용, 다른 지침에 우선)
    public const string PlanMode =
        "Plan mode is active. The user does not want you to execute yet — you MUST NOT make any edits, " +
        "run any non-read-only tools (including changing configs, running commands, making commits, or " +
        "spawning sub-agents), or otherwise change the system. This supersedes any other instructions you " +
        "have received. Use only read-only tools (Read/Glob/Grep) to investigate. When you have enough " +
        "understanding, present a concrete, step-by-step plan and ask the user to approve it. The user will " +
        "switch to act mode to execute — do not execute until then.";

    // 오토-액트 모드 (권한 자동 승인, 자율 실행)
    public const string AutoActMode =
        "Auto-act mode is on. Tool calls are auto-approved — work autonomously to complete the task " +
        "without pausing to ask permission for routine actions. Still exercise care with irreversible or " +
        "destructive operations (deleting data, force-push, dropping tables, etc.): confirm with the user " +
        "before those, even though the system won't prompt.";

    // 플랜 모드 종료 / 액트 모드
    public const string ActMode =
        "## Exited Plan Mode\nYou have exited plan mode. You can now make edits, run tools, and take " +
        "actions (subject to permission prompts).";

    // tool_calls 로 끝났는데 파싱된 툴콜이 0개일 때 (누락/글리치 → 실제 호출 재요청)
    public const string MissingToolCall =
        "You ended your turn indicating a tool call, but no tool call was received. " +
        "If you intended to run a tool to continue the task, make that tool call now. " +
        "Do not just describe what you will do — actually invoke the tool. If the task is " +
        "already complete, briefly report the result instead.";

    // 빈 응답/계속 의도만 있을 때 (query.ts continuation nudge)
    public const string ContinuationNudge =
        "Continue with the task. If you were interrupted, resume your thought. " +
        "Otherwise, use the appropriate tools to proceed to the next step.";

    // 권한 거부 (utils/messages.ts)
    public const string PermissionDenied =
        "Permission for this tool use was denied. The tool use was rejected (eg. if it was a file " +
        "edit, the new content was NOT written to the file). Try a different approach or report the " +
        "limitation to complete your task.";

    // max_turns 도달 후 컨텍스트 압축하고 연장할 때 주입 (작업을 마무리/집중하도록 유도)
    public const string MaxTurnsExtended =
        "<system-reminder>\n" +
        "You have taken many steps and earlier context was automatically compacted to free space. " +
        "Focus on completing the task efficiently: avoid re-reading files you already examined, " +
        "consolidate remaining work, and produce your result or conclusion as soon as you have enough.\n" +
        "</system-reminder>";

    // max_turns 를 끝내 소진했을 때, 빈손으로 멈추지 않도록 툴 없이 마지막 답을 강제한다.
    public const string MaxTurnsFinalAnswer =
        "<system-reminder>\n" +
        "You have reached the step limit and no more tool calls are allowed. " +
        "Do NOT request any tools. Using ONLY what you have already gathered, write your best final " +
        "answer/result now — a concrete conclusion, not a description of what you would do next. " +
        "If something is incomplete, state your findings so far and the remaining unknowns.\n" +
        "</system-reminder>";

    // 성공한 '동일' 읽기 호출 반복 시 1회 주입 — 제자리걸음/토큰낭비 억제.
    public static string DuplicateToolCall(string tool) =>
        "<system-reminder>\n" +
        $"You already ran this exact `{tool}` call in this turn and its result is above. " +
        "Do not repeat identical read-only calls — reuse the earlier result, or change your approach " +
        "(different query/path) if you need new information.\n" +
        "</system-reminder>";

    // 미완료 task 를 남긴 채 종료하려 할 때 — 완료 독려(또는 중단 사유 명시).
    public const string UnfinishedTasks =
        "<system-reminder>\n" +
        "Your task list still has unfinished items (pending or in_progress). Continue and complete them now, " +
        "marking each completed via TaskUpdate as you finish. If you are intentionally stopping, state briefly " +
        "why and what remains — do not end silently with work pending.\n" +
        "</system-reminder>";

    // 툴 실패 루프 가드 (query/toolFailureLoopGuard.ts)
    public static string ToolFailureLoop(string tool, int count) =>
        $"Stopped: repeated tool failures detected.\n\n`{tool}` failed {count} times. " +
        "Please inspect permissions, path, or tool schema before retrying.";
}
