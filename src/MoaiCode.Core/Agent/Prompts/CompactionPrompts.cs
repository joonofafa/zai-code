namespace MoaiCode.Core.Agent.Prompts;

/// <summary>
/// 컨텍스트 컴팩션 지시문 (OpenClaude src/services/compact/prompt.ts 이식).
/// 오래된 대화 구간을 구조화된 9섹션 요약으로 대체할 때 사용. 모델은 &lt;analysis&gt;(작업용)
/// 와 &lt;summary&gt;(최종)를 출력하며, 엔진은 &lt;summary&gt; 본문만 보존한다.
/// </summary>
public static class CompactionPrompts
{
    // 단순 요약 (짧은 구간/폴백용).
    public const string ContextCollapse =
        "You are compacting an older portion of this conversation to save context.\n" +
        "Write a single compact summary of the conversation messages above so that\n" +
        "work can continue without re-reading them. Preserve, concisely:\n" +
        "- decisions made and the reasoning behind them\n" +
        "- the current state, configuration, and any values of record\n" +
        "- file paths, identifiers, commands, and other concrete references touched\n" +
        "- unresolved threads, open questions, and TODOs\n" +
        "- any facts later steps depend on\n" +
        "\n" +
        "Rules: include only information present in the messages above; do not invent\n" +
        "or speculate. No preamble, no headings, no closing remarks — output only the\n" +
        "summary prose. Be substantially shorter than the original.";

    // 구조화 요약 (BASE_COMPACT). 긴 세션에서 결정/파일/오류/사용자메시지/대기작업을 보존.
    public const string BaseCompact =
        "CRITICAL: Respond with TEXT ONLY. Do NOT call any tools. You already have all the context " +
        "you need in the conversation above.\n\n" +
        "Your task is to create a detailed summary of the conversation so far, paying close attention " +
        "to the user's explicit requests and your previous actions. This summary should be thorough in " +
        "capturing technical details, code patterns, and architectural decisions that would be essential " +
        "for continuing development work without losing context.\n\n" +
        "Before providing your final summary, wrap your analysis in <analysis> tags to organize your " +
        "thoughts. In your analysis: chronologically analyze each section, identifying the user's explicit " +
        "requests, your approach, key decisions/concepts/patterns, specific details (file names, code " +
        "snippets, function signatures, edits), errors and how you fixed them, and any user feedback — " +
        "especially where the user told you to do something differently. Then double-check for accuracy " +
        "and completeness.\n\n" +
        "Your summary should include the following sections:\n\n" +
        "1. Primary Request and Intent: Capture all of the user's explicit requests and intents in detail.\n" +
        "2. Key Technical Concepts: List all important technical concepts, technologies, and frameworks.\n" +
        "3. Files and Code Sections: Enumerate specific files/code examined, modified, or created. Include " +
        "full code snippets where applicable and a summary of why each file read/edit matters.\n" +
        "4. Errors and fixes: List all errors and how you fixed them, plus any related user feedback.\n" +
        "5. Problem Solving: Document problems solved and ongoing troubleshooting.\n" +
        "6. All user messages: List ALL user messages that are not tool results — critical for understanding " +
        "feedback and changing intent.\n" +
        "7. Pending Tasks: Outline any pending tasks you have explicitly been asked to work on.\n" +
        "8. Current Work: Describe precisely what was being worked on immediately before this summary, with " +
        "file names and code snippets where applicable.\n" +
        "9. Optional Next Step: The next step directly in line with the user's most recent explicit request " +
        "and the task in progress. Include verbatim quotes from the most recent messages showing exactly " +
        "where you left off. Do not drift onto tangential or already-completed work.\n\n" +
        "Output format — an <analysis> block followed by a <summary> block:\n" +
        "<analysis>\n[your thought process]\n</analysis>\n<summary>\n[the 9 sections above]\n</summary>\n\n" +
        "Output only those two blocks. Include only information present in the conversation above; do not " +
        "invent or speculate.";
}
