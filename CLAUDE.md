# CLAUDE.md

Guidance for agents working in this repository. (Korean, following the existing project docs.)

## What this is

Claude Code(OpenClaude 계열)를 C#/.NET 10 으로 포팅한 코딩·문서 에이전트. CLI(`zaiCode`) + TUI 가 동일한 코어(`QueryEngine`)를 공유한다. 솔루션 = 제품 13개 + 테스트 3개(xUnit) 프로젝트.

**이 브랜치는 서버 의존이 없는 단일 코딩 에이전트다.** upstream moai-code 의 조직 서버 연동(팀 스킬 동기화, `Org*` 툴, `/login`, `EmbeddingClient`/RAG, 서버 경유 WebSearch)과 Avalonia GUI 는 모두 제거했다 — **다시 추가하지 말 것.** 기본 프로바이더는 z.ai GLM Coding Plan이고, 런처(`../zaiCode`)는 `ZAI_API_KEY` 만 폴백으로 넣는다(모델·주소는 넣지 않음).

**문서 주의**: 루트 `README.md`·`PROGRESS.md`, `docs/SERVER_TASK_*.md`·`GUI_*.md`·`HANDOFF_*.md` 는 upstream(조직 서버·GUI·`moai` 바이너리·v1.8.5) 기준이라 **이 브랜치와 맞지 않는다.** 진실은 이 파일과 코드다. 코드 주석에도 `~/.moai` 표기가 다수 남아 있으나(`MemoryTool`, `MoaiLog`, `SkillLoader`, `UsageStore` 등) **권위 있는 경로는 `~/.zaicode`** — 주석이 아니라 `AppPaths` 를 믿을 것.

## 빌드 / 테스트 / 실행

`dotnet` 이 PATH 에 없을 수 있다. `~/.dotnet/dotnet` 또는 `C:\Program Files\dotnet\dotnet.exe`. 핀은 `global.json` = SDK 10.0.100 (`rollForward: latestMajor`).

```bash
dotnet build MoaiCode.sln                  # 기준선: 경고(주로 CS8602 nullable)만, 오류 없음
dotnet test  MoaiCode.sln                  # 전체 (Windows 에서만 Office.Tests 포함)
dotnet test tests/MoaiCode.Core.Tests      # 대부분의 테스트는 여기
dotnet test tests/MoaiCode.Core.Tests --filter "FullyQualifiedName~OpenAiChatModelTests"
dotnet test MoaiCode.sln --filter "DisplayName~이름"

dotnet run --project src/MoaiCode.Cli                          # 대화형 CLI/TUI
dotnet run --project src/MoaiCode.Cli -- run "이 저장소 요약"    # 헤드리스 1회 (텍스트→stdout, 툴 진행→stderr)
```

- **린터/포매터 설정은 없다.** 게이트는 빌드 경고 + 테스트뿐이다.
- 빌드가 hang 하면 VBCSCompiler 데드락 — `-p:UseSharedCompilation=false -nodeReuse:false`.
- TUI 레이아웃 스모크: `tests/run-dock-smoke.sh`(원격/CI 게이트에 연결됨).
- 배포: `./publish-win.sh [rid]`, `./publish-linux.sh`, `./publish-all.sh` → `dist/<rid>/zaiCode[.exe]` self-contained 단일 파일 + SHA-256 + `THIRD_PARTY_NOTICES.md`. 서명은 별도 단계.
- 키·모델이 없으면 `EchoChatModel` 로 폴백(상태줄 "Offline (/provider)") → **오프라인에서도 빌드/테스트/실행 가능**. 테스트는 `MOAI_HOME` 으로 홈을 격리한다.

## 아키텍처

**핵심 루프 — `src/MoaiCode.Core/Agent/QueryEngine.cs`**: 모델 SSE 스트리밍 → 권한 게이트(`IPermissionGate`) → 툴 디스패치 → 결과 주입 → 재진입(멀티턴). 상태를 가진 메시지 스토어를 소유한다. 컨텍스트가 창의 ~70% 를 넘으면 선제 컴팩션하고 원래 사용자 요청(`_goal`)을 재고정(re-anchor)해 표류를 막는다. mid-stream 끊김 재시도·max_turns 연장·context-overflow 복구도 여기.

**진입점 흐름**: `Program.cs`(System.CommandLine; 인자 없으면 REPL) → `AppBootstrap`(설정·프로바이더·툴 셋·스킬·MCP·프롬프트 컨텍스트) → `QueryEngine` → TUI(`MoaiCode.Tui`) 또는 `HeadlessRunner`/`StreamJsonRunner`.

**데이터 루트의 유일한 조립 지점 — `Core/AppPaths.cs`**: 설정·로그·세션·메모리·자격증명 경로는 전부 `AppPaths.Combine(...)` 으로 만든다. 기본은 `~/.zaicode`, `MOAI_HOME` 이 있으면 그쪽(테스트 격리용 — Windows 에서는 `HOME` 변경으로 격리되지 않는다). 경로 문자열을 직접 조립하지 말 것.

**프로바이더 접속 정보의 유일한 해석지점 — `Config/ProviderResolver.cs`**: 결과 `ProviderProfile` 은 `ActiveProvider.Current`(Core)에 두고, 대화 모델·WebSearch·이미지 툴은 **거기서만** 읽는다. 환경변수를 직접 읽지 말 것(규칙이 갈리면 한쪽만 조용히 죽는다). 우선순위: `--model` → 저장값 → 환경변수 → 기본값. 프로바이더별 고정값(id·기본 주소·키 이름·기본 컨텍스트 창)은 `Core/ProviderCatalog.cs`, z.ai 전용 값(코딩 엔드포인트 화이트리스트·모델별 컨텍스트 창 표·비전 기본값)은 `Core/ZaiEndpoint.cs`.

**프로바이더** — `/provider` 로 Z.ai·OpenAI·Claude·OpenRouter·vLLM 중 고르고, 키는 붙여넣기로 받는다(`SecretPrompt`, 화면엔 앞 8자만). 모델 목록 조회로 접속을 확인한 뒤에만 저장한다(실패·취소 시 아무것도 안 바꿈).
- 키는 `credentials.json` 에 프로바이더별(`ZAI_API_KEY`·`OPENAI_API_KEY`·`ANTHROPIC_API_KEY`·`OPENROUTER_API_KEY`·`VLLM_API_KEY`), 선택값은 `settings.json` 의 `activeProvider` + `providers.<id>.{model, visionModel, baseUrl(vLLM 전용), contextWindow}`. 이 둘은 **사용자 레이어에서만** 읽는다(프로젝트 설정이 키를 다른 주소로 보내지 못하게). 옛 최상위 `provider`/`baseUrl` 은 읽지 않는다.
- z.ai 외 프로바이더는 `MOAI_MODEL` 을 보지 않는다(옛 런처의 glm-5.3 이 새지 않게).
- 대화 중 교체는 `SwitchableChatModel` 의 안쪽만 바꾼다(엔진·하위 에이전트·위험 분류기가 같은 인스턴스를 쥠). 컨텍스트 창은 모델 목록이 알려 주면 저장하고 `QueryEngine.SetContextWindow` 로 즉시 반영, 아니면 z.ai 표 → 프로바이더 기본값.

**모델 계층 — `MoaiCode.Providers`**: `ProviderFactory.Create(profile)` → `RetryingChatModel`(pre-yield 끊김만 지수 백오프) → 전송. 헤더 대기 상한·연결 실패 분류는 `Http/StreamingHttp` 공용.
- Z.ai·OpenAI·OpenRouter·vLLM = `OpenAiChatModel`(OpenAI 호환). 차이: z.ai 만 `max_tokens` 고정·`tool_stream`, OpenAI 는 `max_completion_tokens`(`MOAI_MAX_TOKENS` 지정 시만), OpenRouter 는 `reasoning.effort`, 나머지는 `stream_options.include_usage`. 서버가 선택 필드를 400 으로 거부하면 빼고 재전송(모델별 기억).
- Claude = `Anthropic/AnthropicChatModel`(Messages API, 원시 HTTP). thinking 블록은 서명째 `ReasoningBlock`(Core)으로 받아 어시스턴트 메시지 앞에 저장하고 다음 요청에 되돌려 보낸다. 컴팩션으로 기록이 바뀌므로 `thinking.block_binding.prefix_mismatch_behavior=drop_block` 를 보낸다.

**WebSearch 는 대화 프로바이더의 내장 검색** (`Tools/Web/WebSearchTool.cs` + `ProviderWebSearch.cs`): z.ai 는 `tools=[{type:web_search}]`(결과는 최상위 `web_search` 배열, 엔진 `MOAI_SEARCH_ENGINE`, 기본 `search-prime`), OpenAI 는 Responses API, OpenRouter 는 `openrouter:web_search`, Claude 는 `web_search_20260209`. 별도 검색 키 없음. vLLM 은 검색이 없어 툴을 숨긴다 — **프로바이더에 따라 못 쓰는 툴은 `IConditionalTool`** 로 엔진이 요청마다 걸러 모델에게 보이지 않는다(이미지 생성 = Z.ai 전용, 이미지 분석 = 비전 모델을 골랐을 때만).

**툴 셋은 프론트엔드별로 다르고, 이게 곧 보안 경계다**: 원장은 `MoaiCode.Tools/ToolRegistry.cs` 의 `BuiltIn`. CLI 는 `BuiltIn` + `BashTool`(전체 권한). destructive 툴(Bash)은 별도 어셈블리(`MoaiCode.Tools.Bash`)에서 합류한다. 툴 셋을 넓히는 변경은 권한/보안 검토를 동반할 것.

**Bash 권한 규칙(세그먼트 판정)** — `Config/PermissionRules.cs` + `Core/Tools/PermissionRule.cs`: 복합 명령(`ls | tail`, `a && b`)은 allow 패턴이 전체와 매치되지 않으므로 **모든 세그먼트**가 allow 규칙 또는 내장 읽기 전용 명령(ls/cat/grep/head/tail/wc/find/sort/diff 등)으로 덮일 때만 자동 허용한다(`AllSegmentsAllowed`). 하나라도 안 덮이면 확인으로 넘어간다. **명령치환(`$(...)`, 백틱)·리다이렉션이 있으면 완화하지 않는다**(`SegmentsAreTrustworthy`). 두 함수의 세트를 항상 같이 고려할 것.

**셸 보안은 단순화 모델**: `Tools.Bash/BashSecurity.cs` 는 풀 AST 파싱 대신 고위험 destructive 패턴 deny-list 로 막고, 나머지는 권한 모드(`Ask`/`Auto`/`Deny`, **기본 `Ask`**)에 위임한다. 정책: 불확실하면 보수적으로. (과거 정규식 백트래킹으로 긴 파이프라인에서 수백 초 멈춘 사고가 있었으니 새 패턴은 선형 시간인지 확인할 것.)

**로컬 청킹/벡터 검색**: `ChunkBuild` + `.moai-chunks/*.vec` float32 + `vectors.json`(포맷 `docs/CHUNK_VEC_FORMAT.md`) + 오프라인 코사인 `ChunkSearch`. **CLI 는 임베딩하지 않는다** — 질의 벡터는 외부 파이프라인이 만들어 넘긴다(z.ai Coding Plan 에 임베딩 모델이 없음).

**설정 병합**(뒤가 우선): `~/.claude/settings.json` → `~/.zaicode/settings.json` → `<workspace>/.claude/settings.json` → 환경변수. JSONC 허용. 자격증명은 `AppPaths` 아래 `credentials.json` — **직접 읽지 말고 `ICredentialStore`/`FileCredentialStore` 를 경유**.

**세션·메모리는 cwd 별로 스코핑**된다(`Core/Memory/ProjectMemory.cs`): `~/.zaicode/projects/<percent-encoded-cwd>/{sessions,memory}`. 슬러그는 절대경로 percent-encoding 이며 git 탐색을 쓰지 않는다. `MEMORY.md` 인덱스가 매 시스템 프롬프트에 주입된다.

**프롬프트 조립**: `Core/Agent/Prompts/SystemPromptBuilder.cs` 는 순수 함수, `PromptContext` 를 `AppBootstrap` 이 IO 로 채운다. 여기서 `CLAUDE.md`/`AGENTS.md` 를 읽어 합치고 **총 길이를 제한**한다 → **이 파일을 짧게 유지해야 하는 이유**. 리포맵(`RepoMapBuilder`)도 여기서 붙는다.

## 코딩 원칙

**재활용 우선(DRY)**. 새 로직 전에 같은 일을 하는 코드가 있는지 확인하고 있으면 쓴다. 복붙·병렬 구현이 보이면 멈추고 공용 헬퍼로 추출한다(예: `/model`·`/skills` 가 공유하는 `ModelPicker`/`SkillPicker`). **UI 위젯·선택 플로우·설정 저장은 한 곳에 두고 콜백으로 주입**한다.

- **Core 는 Config 를 참조할 수 없다**(순환). Core 진단 로그는 CLI 가 `MoaiLog.Info` 를 `Action<string>` 으로 주입한다.
- 파일 쓰기는 `Core/AtomicFile`, 스트리밍 HTTP 는 `Core/Web/ToolHttp` 를 재사용한다(병렬 HttpClient/임시파일 구현 금지).
- 새 툴은 `MoaiCode.Tools`(+필요시 별도 어셈블리)에 두고 `ToolRegistry` 에 등록하며, 경로 검증은 `PathSafety` 를 재사용한다.

## 로컬라이제이션

기본(base) 언어는 **영어**다. 소스의 사용자 노출 문자열은 **반드시 영어로 작성**(하드코딩 한글 금지), 한국어는 `Resources/ko.json` 번역 레이어. `en.json` 이 진실의 원천이며 **en/ko 키는 항상 완전 일치**. 접근은 `L10n.Get(key, args)`, 키 규칙은 `cli.*`/`slash.*`/`common.*` — **문장을 키로 쓰지 않는다**. 언어 결정은 설정 → `MOAI_LANGUAGE` → 기본 `en`. `InvariantGlobalization=true` 라서 위성 어셈블리/`CurrentUICulture` 대신 명시적 언어코드 + 임베드 JSON 을 쓴다. 상세: `docs/LOCALIZATION.md`.

영어 UI 라벨은 sentence case("Selected"). 단 명령어 미러(`install:`), 프로그램 프리픽스(`zaiCode:`), 괄호 상태(`(none)`), 기술 토큰/경로(`.mcp.json`)는 소문자 유지.

## 로깅

**로그는 항상 영어 ASCII.** 한글 등 비-ASCII 를 로그에 넣지 말 것(Windows CP949 콘솔/파일 깨짐). 사용자 입력 원문도 넣지 말고 길이·해시 등 메타데이터로 대체한다. 로거는 `Config/MoaiLog` (`AppPaths` 아래 `logs/`), 레벨은 설정 `logLevel`.

## 멀티타깃 & 플랫폼 (자주 걸림)

- `Directory.Build.props` 가 모든 프로젝트에 `net10.0` 을 강제하되 **`MoaiCode.Cli` 만 예외**(`net10.0;net10.0-windows`). props 는 프로젝트 본문보다 먼저 평가되므로 프로젝트 이름으로 분기한다.
- **`MoaiCode.Tools.Office` 는 Windows 전용**(Office COM, `net10.0-windows`). CLI 는 이 프로젝트를 참조하지 않는다(참조자는 `tests/MoaiCode.Office.Tests` 뿐). Linux 에서는 컴파일 검증만 — COM 변경은 Windows 실기 검증 필요.
- 패키지 버전은 **중앙 관리**(`Directory.Packages.props`, `ManagePackageVersionsCentrally=true`) — 개별 csproj 에 버전 쓰지 말 것. 여기 남은 Avalonia 항목은 제거된 GUI 잔재다.
- 이미지 처리는 `StbImageSharp` + 자체 `Raster` 를 쓴다(ImageSharp 는 라이선스/보안 이유로 제거됨 — 되돌리지 말 것). 서드파티 고지는 `THIRD_PARTY_NOTICES.md` / `/license`.
- `ConsoleSetup.EnsureUtf8()` 이 모든 출력보다 먼저 호출돼야 한다(Windows CP949 에서 ❯/✓/• 깨짐). 새 진입점도 유지할 것.

## 디렉터리

```text
src/
  MoaiCode.Core           에이전트 루프(QueryEngine), Messages, Security(권한·위험분류), Memory,
                          AppPaths(데이터 루트)·AtomicFile, ProviderCatalog, ZaiEndpoint, ActiveProvider
  MoaiCode.Providers      ProviderFactory, OpenAi/Anthropic 모델, ModelDirectory, Switchable/Retrying, Echo 폴백
  MoaiCode.Tools          툴 원장(ToolRegistry.BuiltIn): Files·Search·Web·Media·Tasks·Agent + PathSafety
  MoaiCode.Tools.Bash     BashTool + 셸 보안 정책 + 백그라운드 셸(BashOutput/KillShell)
  MoaiCode.Tools.OpenXml  Docx/Pptx/Xlsx 생성·편집·검사, ChunkBuild/Fetch/Search, 이미지 삽입
  MoaiCode.Tools.Office   Windows Office COM — net10.0-windows 전용
  MoaiCode.Mcp            MCP stdio + 스킬/플러그인 로더(./.claude, ~/.claude, ~/.zaicode)
  MoaiCode.Config         Settings(3단 병합)·SettingsWriter, ProviderResolver, CredentialStore, SecureFile,
                          PermissionRules, MoaiLog, ProxyConfig
  MoaiCode.Localization   L10n.Get + Resources/en.json(base)·ko.json
  MoaiCode.Persistence    Session/History/Checkpoint/Usage 저장(JSONL/JSON)
  MoaiCode.Tui            Spectre.Console REPL, LineEditor, 슬래시 커맨드(/provider·/model·/theme·/license),
                          raw 모드 입력(VT 파서, bracketed paste), 테마
  MoaiCode.Cli            진입점·AppBootstrap·CliProviderSetup, HeadlessRunner, StreamJsonRunner(NDJSON), 번들 스킬
  MoaiCode.Sdk            공개 SDK 계약(MoaiCodeClient)
tests/
  MoaiCode.Core.Tests     Core+CLI+TUI+MCP 통합 테스트 대부분 (테스트는 주로 여기)
  MoaiCode.OpenXml.Tests  문서 생성/편집 왕복·레이아웃·인젝션
  MoaiCode.Office.Tests   STA 디스패처·COM 계약 (Windows 전용)
  dock-smoke/, dock-resize-smoke/  TUI 레이아웃 스모크 (run-dock-smoke.sh)
demo/                     단일 샘플(RewardCalculator.cs) — 에이전트 자기 테스트용
docs/                     로컬라이제이션 규칙, 청킹 벡터 포맷 등 (SERVER_TASK_*/GUI_* 는 upstream 이야기)
dist/, publish/           게시 산출물 — 커밋하지 않는다
```

## 테스트 관례

xUnit `[Fact]`/`[Theory]`, 이름은 `MethodName_condition_expectedResult`. 동작이 속한 프로젝트에 둔다. 보안·툴 디스패치·영속성·로컬라이제이션·문서 변경에는 회귀 테스트를 붙인다. 커버리지 임계값은 없다. 환경변수/콘솔을 건드리는 테스트는 `EnvMutating` 컬렉션으로 직렬화하고 홈은 `MOAI_HOME` 으로 격리한다.

## 커밋

Conventional Commits 스타일의 간결한 명령형 제목(`feat(cli): …`, `fix(tui): …`, `docs: …`, `chore: …`). 커밋은 집중적으로. 자격증명·로컬 설정·게시 산출물은 커밋하지 않는다.
