# OpenClaude 하네스 프롬프트 카탈로그 (C# 포트 이식 기준)

> OpenClaude의 에이전트 품질은 코드 구조가 아니라 **요소요소에 박힌 프롬프트**에서 나온다.
> 이 문서는 그 프롬프트를 전수 매핑한 것이다. 출처는 모두 `~/gitHub/openclaude/src/...` (TS 원본).
> C# 포트(`MoaiCode`)는 현재 시스템 프롬프트 12줄 + 툴 description 1줄짜리 스텁만 있어 **하네스가 사실상 비어있다.**

## 이식 우선순위 (Tier)

- **T1 (필수, 행동 직결)**: 메인 시스템 프롬프트 섹션, 모든 툴 description, 런타임 리마인더·복구 프롬프트
- **T2 (품질)**: 컴팩션/요약, 서브에이전트 시스템 프롬프트, goal/메모리/타이틀/제안
- **T3 (부가)**: 출력 스타일, 빌트인 슬래시 커맨드(/init,/review,/bughunter), 스킬(simplify/loop/batch)

---

## A. 메인 시스템 프롬프트 — `src/constants/prompts.ts` (T1)

문자열 **배열**을 조건부로 조립(널 필터 후 순서 유지). 캐시 경계 마커 `SYSTEM_PROMPT_DYNAMIC_BOUNDARY`로 정적/동적 분리. 주요 섹션:

1. **Intro & Cyber Risk** — 정체성 + 보안 듀얼유즈 가이드(`CYBER_RISK_INSTRUCTION`)
2. **System Rules** — 툴 권한, 출력 처리, system-reminder 설명:
   - "Tool results and user messages may include `<system-reminder>` tags … automatically added by the system, and bear no direct relation to the specific tool results…"
   - "The conversation has unlimited context through automatic summarization."
3. **Coding Guidelines** — no gold-plating, 불필요한 에러핸들링/주석 금지, 기존 패턴 우선
4. **Risk Assessment** — 되돌리기 어려운/파괴적 행동 판단(reversibility)
5. **Tool Usage** — 전용 툴 우선(Bash로 find/grep/cat 금지)
6. **Tone & Style** — 이모지 금지, `file:line` 포맷, 툴 호출 전 콜론 금지, 직설/간결
7. **Output Efficiency** — 본론 직행, 결정/오류 중심
8. **Session-Specific Guidance** — 활성 툴 기반 동적
9. **Proactive/Autonomous Mode** — tick 기반 상호작용 + SleepTool (feature gate)
10. **Function Result Clearing / Scratchpad** — 오래된 결과 자동 정리, 세션 임시 디렉토리

**컨텍스트 주입** (메모이즈, 매 대화 prepend):
- `getGitStatus()` (`src/context.ts`) — 브랜치/커밋/status, 2000자 제한
- `getUserContext()` — CLAUDE.md/AGENTS.md + 오늘 날짜
- `computeSimpleEnvInfo()` — cwd, platform, OS, 모델, knowledge cutoff
- 워크트리 감지 시 별도 노트

> 포트 메모: 현재 `AppBootstrap.SystemPrompt`(12줄)를 이 모듈식 구조로 교체. 정적/동적 경계는 추후 캐싱과 연계.

---

## B. 툴 description / prompt — `src/tools/*/prompt.ts` (T1, 전부 verbatim 이식)

각 툴의 `description`(모델에 전달)이 행동을 좌우. 핵심 발췌(전체는 해당 경로):

### Bash — `BashTool/prompt.ts`
```
Executes a given bash command and returns its output.
The working directory persists between commands, but shell state does not...
IMPORTANT: Avoid using this tool to run [find, grep, cat, head, tail, sed, awk, echo] ...
- File search: Use Glob (NOT find or ls)
- Content search: Use Grep (NOT grep or rg)
- Read files: Use Read (NOT cat/head/tail)
- Edit files: Use Edit (NOT sed/awk)
- Write files: Use Write (NOT echo >/cat <<EOF)
... (+ sandbox / git commit·PR 가이드)
```

### Read — `FileReadTool/prompt.ts`
"Reads a file from the local filesystem… file_path must be absolute… up to 2000 lines… cat -n format with line numbers… can read images/PDF/Jupyter… empty file → system reminder warning." (offset/limit/pages 필드 설명 포함)

### Write — `FileWriteTool/prompt.ts`
"…overwrite existing… MUST Read first or fails… Prefer Edit for existing files (diff만 전송)… NEVER create *.md/README unless explicitly requested… emoji 금지 unless asked."

### Edit — `FileEditTool/prompt.ts`
"Performs exact string replacements… must Read first… preserve exact indentation AFTER line-number prefix… FAIL if old_string not unique → add context or replace_all… emoji 금지."

### Glob — `GlobTool/prompt.ts`
"Fast file pattern matching… `**/*.js`… sorted by mtime… open-ended search는 Agent 툴 사용."

### Grep — `GrepTool/prompt.ts`
"Built on ripgrep… ALWAYS use Grep, NEVER `grep`/`rg` via Bash… regex/glob/type… output_mode content|files_with_matches|count… multiline 옵션… 리터럴 중괄호 이스케이프."

### WebFetch — `WebFetchTool/prompt.ts`
"Fetch URL → HTML→markdown → 소형모델로 처리… MCP web fetch 우선… HTTP→HTTPS 승격… 15분 캐시… 리다이렉트 시 새 요청… GitHub URL은 gh CLI 우선."

### WebSearch — `WebSearchTool/prompt.ts`
"…**MANDATORY** end with 'Sources:' 섹션 (markdown 링크)… US만 지원… 현재 연도 사용 강제."

### Task 계열 — `TaskCreate/List/Get/Update/prompt.ts`
구조화된 태스크 리스트 관리(언제 쓰고/안 쓰는지, 필드 subject/description/activeForm, 상태 워크플로 pending→in_progress→completed, blocks/blockedBy).

### Agent — `AgentTool/prompt.ts`
"Launch a new agent to handle complex, multi-step tasks… fork(자기복제) vs fresh subagent 기준… 리서치는 fork, 구현도 fork 우선… 프롬프트 작성 가이드."

### Skill — `SkillTool/prompt.ts`
"…사용자가 '/something' 언급 시 스킬… BLOCKING REQUIREMENT: 매칭되면 다른 응답 전에 Skill 툴 먼저 호출… `<command-name>` 태그 보이면 이미 로드됨."

### TodoWrite, AskUserQuestion, Notebook, ToolSearch, LSP, MCP(List/Read), SSE/Sleep/Monitor, EnterPlanMode/ExitPlanMode, EnterWorktree/ExitWorktree, Snip, ScheduleCron(Create/Delete/List), Team(Create/Delete), Brief(SendUserMessage), SendMessage, PowerShell, RemoteTrigger
→ 각 `*/prompt.ts`에 verbatim 존재. (포트 시 우리 6개 툴부터: Read/Write/Edit/Glob/Grep/Bash description을 위 원문으로 교체)

**특기**: Snip(`SnipTool/prompt.ts`) — 컨텍스트 절약용 메시지 제거, `snip_id` 메타 사용 규칙.

---

## C. 런타임 주입 리마인더 & 복구 프롬프트 (T1) — 가장 미묘하고 중요

대부분 `isMeta:true` 사용자 메시지 또는 `<system-reminder>` 래핑. 위치/원문:

| # | 트리거 | 출처 | 원문 핵심 |
|---|---|---|---|
| 1 | 빈 응답/계속 의도만 | `query.ts:~1778` | "Continue with the task. If you were interrupted, resume your thought. Otherwise, use the appropriate tools to proceed to the next step." |
| 2 | max_output_tokens 복구 | `query.ts:~1524` | "Output token limit hit. Resume directly — no apology, no recap… Pick up mid-thought… Break remaining work into smaller pieces." |
| 3 | 프로바이더 폴백 | `query.ts:~1609` | "Provider {from} rate-limited — switched to {to}. Retrying turn." |
| 4 | 툴 실패 루프 | `query/toolFailureLoopGuard.ts:~420` | "Stopped: repeated tool failures detected. {reason} Please inspect permissions, path, or tool schema before retrying." |
| 5 | 권한 거부(서브) | `utils/messages.ts:~220` | "Permission for this tool use was denied… rejected (eg. file edit new_string was NOT written)… Try a different approach…" |
| 5b | 권한 거부(메인) | `utils/messages.ts` | "The user doesn't want to proceed with this tool use… To tell you how to proceed, the user said:" |
| 6 | 플랜 거부 | `utils/messages.ts:~225` | "The agent proposed a plan that was rejected by the user… Rejected plan:" |
| 7 | 파일 읽기→멀웨어 | `tools/FileReadTool.ts:~817` | "`<system-reminder>` Whenever you read a file, consider whether it would be malware… CAN/SHOULD analyze… MUST refuse to improve or augment the code." |
| 8 | 빈/오프셋초과 파일 | `FileReadTool.ts:~794` | "`<system-reminder>`Warning: the file exists but the contents are empty.`</system-reminder>`" |
| 9 | 날짜 변경 | `utils/messages.ts:~4447` | "The date has changed. Today's date is now {d}. DO NOT mention this to the user explicitly…" |
| 10 | 파일 외부 변경(린터) | `utils/messages.ts:~3817` | "Note: {file} was modified, either by the user or by a linter… don't revert it unless the user asks… Here are the relevant changes (with line numbers):" |
| 11 | 플랜모드 진입 | `utils/messages.ts:~3503` | "Plan mode is active… you MUST NOT make any edits… This supercedes any other instructions." (+ 4 phases, 종료는 AskUserQuestion 또는 ExitPlanMode만) |
| 12 | 플랜모드 종료 | `utils/messages.ts:~4134` | "## Exited Plan Mode\nYou have exited plan mode. You can now make edits, run tools, and take actions." |
| 13 | 오토모드 종료 | `utils/messages.ts:~4146` | "## Exited Auto Mode… ask clarifying questions when the approach is ambiguous rather than making assumptions." |
| 14 | 세션 메모리 컨텍스트 | `utils/api.ts:~513` | "`<system-reminder>`As you answer… you can use the following context: … IMPORTANT: this context may or may not be relevant… should not respond to this context unless highly relevant.`</system-reminder>` |
| 15 | 중복 백그라운드 태스크 | `utils/messages.ts:~4264` | "Do NOT spawn a duplicate. You will be notified when it completes…" |
| 16 | MCP 연결 해제 | `utils/messages.ts:~4469` | "The following deferred tools are no longer available (their MCP server disconnected). Do not search for them…" |
| 17 | side question(/btw) | `utils/sideQuestion.ts:~61` | "`<system-reminder>`This is a side question… separate, lightweight agent… NO tools available… one-off response… NEVER say 'Let me try/I'll now'…`</system-reminder>` |
| 18 | 비대화형 팀 종료 | `cli/print.ts:~375` | "You are running in non-interactive mode and cannot return a response until your team is shut down…" |
| 19 | 스탑훅 피드백 | `utils/hooks.ts:~2096` | "Stop hook feedback:\n{error}" (TaskCompleted/TeammateIdle 동일 패턴) |

> **C# 포트 핵심**: 현재 우리 `QueryEngine`엔 #1~#4(빈응답/복구/폴백/실패루프) 상태머신과 메시지가 전무. 우리가 본 "안녕하세요" 군더더기 문제도 결국 시스템 프롬프트 + 이런 리마인더 부재 탓. **T1으로 #1,#5,#7,#8,#10이 최우선**(파일툴/권한/멀웨어/린터 알림).

---

## D. 컴팩션·메모리·목표·타이틀 (T2)

### 컨텍스트 collapse — `src/services/contextCollapse/ctxAgentPrompt.ts`
```
You are compacting an older portion of this conversation to save context.
Write a single compact summary … Preserve, concisely:
- decisions made and the reasoning behind them
- the current state, configuration, and any values of record
- file paths, identifiers, commands, and other concrete references touched
- unresolved threads, open questions, and TODOs
- any facts later steps depend on
Rules: include only information present above; do not invent… output only the summary prose. Be substantially shorter than the original.
```

### 본격 컴팩션 3종 — `src/services/compact/prompt.ts`
- `BASE_COMPACT_PROMPT` (143줄, 9섹션 구조화 요약)
- `PARTIAL_COMPACT_PROMPT` (최근분만)
- `PARTIAL_COMPACT_UP_TO_PROMPT` (캐시 친화 prefix 요약)
→ 길어서 원본 참조. 임계값: API microcompact 경고 180K, 타깃 40K.

### 메모리 추출 — `src/services/extractMemories/prompts.ts`
`buildExtractAutoOnlyPrompt` / `buildExtractCombinedPrompt`. 메모리 타입 user/feedback/project/reference, frontmatter 포맷, "What NOT to save"(코드패턴/git히스토리/CLAUDE.md 중복 등). (우리 메모리 시스템과 동형!)

### 오토 드림(통합) — `src/services/autoDream/consolidationPrompt.ts`
4단계(Orient→Gather→Consolidate→Prune/Index), MEMORY.md 인덱스 ≤25KB/한 줄 ≤150자.

### 목표 — `src/services/goal/evaluator.ts` + `instructions.ts`
시스템 프롬프트: "You evaluate whether a coding agent has completed a session goal. Return strict JSON only {complete,confidence,reason,next_instruction}…" (JSON schema 강제, Haiku 사용). start/continuation instruction 별도.

### 세션 타이틀 — `src/utils/sessionTitle.ts`
"Generate a concise, sentence-case title (3-7 words)… Return JSON {title}." (good/bad 예시 포함)

### 프롬프트 제안 — `src/services/PromptSuggestion/promptSuggestion.ts`
"[SUGGESTION MODE] Predict what the USER would type next… 2-12 words… NEVER suggest evaluative/questions/Claude-voice…"

### 서브에이전트 진행요약 — `src/services/AgentSummary/agentSummary.ts`
"Describe your most recent action in 3-5 words present tense (-ing). Name the file/function… Good: 'Reading runAgent.ts'…"

### 세션 메모리 파일 업데이트 — `src/services/SessionMemory/prompts.ts`
구조 보존(헤더/이탤릭 설명 줄 유지, 내용만 갱신), 템플릿 섹션(Current State/Task spec/Files/Workflow/Errors/Learnings/Key results/Worklog).

---

## E. 서브에이전트 시스템 프롬프트 — `src/tools/AgentTool/built-in/*` (T2)

- **general-purpose** (`generalPurposeAgent.ts`): "You are an agent for OpenClaude… Complete the task fully—don't gold-plate, but don't leave it half-done… 강점/가이드라인(broad→narrow, NEVER create *.md)."
- **Explore** (`exploreAgent.ts`): "file search specialist… === CRITICAL: READ-ONLY MODE === STRICTLY PROHIBITED from creating/modifying/deleting… fast agent… parallel tool calls."
- **Plan** (`planAgent.ts`): "software architect… READ-ONLY… Process: Understand→Explore→Design→Detail… End with 'Critical Files for Implementation' (3-5)."
- **Verification** (`verificationAgent.ts`, ~120줄): "Your job is not to confirm it works — it's to try to break it… two failure patterns(verification avoidance, seduced by first 80%)… type별 검증 전략… adversarial probes… 출력 'VERDICT: PASS|FAIL|PARTIAL'."
- **claude-code-guide** (`claudeCodeGuideAgent.ts`): 문서 안내(Claude Code/Agent SDK/Claude API).
- **fork** (`forkSubagent.ts`): "`<fork>` STOP. READ THIS FIRST. You are a forked worker process, NOT the main agent… ignore 'default to forking'… report once at end… begin with 'Scope:'." + 워크트리 notice.
- **statusline-setup**: 상태줄 설정 변환 가이드.

---

## F. 출력 스타일 / 슬래시 커맨드 / 스킬 (T3)

- **출력 스타일** — `src/constants/outputStyles.ts`
  - Explanatory: "★ Insight ──…" 블록으로 교육적 설명 삽입
  - Learning: 사용자에게 직접 코드 작성 요청(hands-on)
- **빌트인 커맨드** — `src/commands/`
  - `/init` (OLD/NEW_INIT_PROMPT, 220줄): CLAUDE.md 생성
  - `/review` (LOCAL_REVIEW_PROMPT): gh pr diff 기반 리뷰
  - `/bughunter`, `/bughunter-security`, `-perf`: 다단계 버그헌트(map→hunt→skeptic→fix), confidence 게이팅
  - `/security-review`: OWASP, HIGH-confidence(>80%)만
- **번들 스킬** — `src/skills/bundled/`
  - `simplify`: 3개 리뷰 에이전트 병렬(reuse/quality/efficiency) → 수정
  - `loop`: 유지보수 루프(.claude/loop.md 따름)
  - `batch`: 병렬 워커 오케스트레이션

---

## 현재 C# 포트 갭 & 이식 계획

| 영역 | OpenClaude | MoaiCode 현재 | 액션 |
|---|---|---|---|
| 시스템 프롬프트 | 11+ 섹션 모듈식 + 컨텍스트 주입 | 12줄 하드코딩 | A 섹션 모듈식으로 재작성 (`SystemPromptBuilder`) |
| 툴 description | 풀 텍스트(각 prompt.ts) | 1줄 스텁 | B의 6개 핵심(Read/Write/Edit/Glob/Grep/Bash) verbatim 교체 |
| 런타임 리마인더 | 24종 | 없음 | C의 #1,#5,#7,#8,#10 우선 + QueryEngine 복구 상태머신 |
| 컴팩션/메모리/goal | 다수 | 없음 | T2, 멀티턴 길어질 때 |
| 서브에이전트 프롬프트 | 7종 | AgentTool 시스템프롬프트 1줄 | Explore/Plan/general/fork verbatim |
| 출력스타일/커맨드/스킬 | 다수 | 없음 | T3 |

**이식 진행 상황 (2026-06-26)**:
- ✅ **T1a 완료** — `MoaiCode.Core/Agent/Prompts/SystemPromptBuilder.cs`(intro+cyber/system/doing-tasks/actions/using-tools/tone/output-efficiency/environment/CLAUDE.md) + `PromptContext`, `AppBootstrap.BuildPromptContext`(git/OS/date/CLAUDE.md 수집)
- ✅ **T1b 완료** — 6개 툴 description 원문 정합 교체(Read/Write/Edit/Glob/Grep/Bash) + Agent description
- ✅ **T1c 완료** — `Reminders.cs`(빈파일/멀웨어 system-reminder, 권한거부 문구, continuation nudge, tool-failure-loop) → FileReadTool/QueryEngine 연결
- ✅ **T2 완료** — `CompactionPrompts.ContextCollapse` + QueryEngine 안전 컴팩션(꼬리 UserMessage 경계) + `SubAgentPrompts`(general/explore/plan) AgentTool subagent_type 연결
- 검증: 68개 테스트 통과, OpenRouter 라이브에서 전용툴 우선·간결 응답 확인
- ✅ **T3 완료** — read-before-edit 강제(`ReadTracker`), Verification 서브에이전트, 출력스타일(Explanatory/Learning, `OutputStyles`+settings), 프롬프트형 슬래시(`/init`,`/review`, SlashResult.SubmitPrompt)
- ✅ **TUI 마크다운 렌더링** — `MarkdownRenderer`(Markdig→Spectre: 헤딩/문단/리스트/코드블록패널/인용/표/굵게·기울임·인라인코드·링크), 스트리밍 중 원문→완료 시 렌더 교체
- 검증: 76개 테스트 통과
- ⏳ **남은 것**: 본격 컴팩션 3변형(BASE/PARTIAL), 메모리추출/goal/타이틀/제안 프롬프트, /bughunter류, 번들스킬(simplify/loop/batch), 코드블록 신택스 하이라이트

> 원문 전체는 위 `src/...` 경로에서 직접 가져와 이식한다(이 문서는 지도 + 핵심 발췌).
