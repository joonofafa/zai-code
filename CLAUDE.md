# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

# Zai Code 프로젝트 지침 (moai-code 의 z.ai 전용 브랜치)

Claude Code(OpenClaude 계열 에이전트)를 C#/.NET 10 으로 포팅한 코딩·문서 에이전트. CLI(`zaiCode`) + TUI 가 동일한 코어(`QueryEngine`)를 공유한다. OpenAI 호환 SSE 스트리밍 모델을 사용한다.

**이 브랜치(`zai`)는 서버 의존이 없는 단일 코딩 에이전트다.** upstream moai-code 에 있던 조직 서버(open-moai) 연동 — 팀 공유 스킬 동기화, 조직 문서함/RecordDB 툴(`Org*`), 서버 로그인 플로우(`/login`), 서버 임베딩(`EmbeddingClient`) 과 그에 의존하던 RAG 툴, 서버 경유 `WebSearch`, Avalonia GUI — 는 모두 제거했다. **다시 추가하지 말 것.** 백엔드는 z.ai GLM Coding Plan(`https://api.z.ai/api/coding/paas/v4`)이며, 키·모델은 런처(`../zaiCode`)가 환경변수로 주입한다.

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
- API 키가 없으면 `EchoChatModel` 로 폴백(오프라인 개발 가능). 실제 모델은 `OPENAI_API_KEY` + `OPENAI_BASE_URL` (+ `MOAI_MODEL`) 환경변수.

## 멀티타깃 & 플랫폼 (자주 걸리는 부분)

- `Directory.Build.props` 가 모든 프로젝트에 `net10.0` 을 강제하되 **`MoaiCode.Cli` 만 예외**(멀티타깃 `net10.0;net10.0-windows`). props 는 프로젝트 본문보다 먼저 평가되므로 프로젝트 이름으로 분기한다.
- **Office COM 툴(`MoaiCode.Tools.Office`)은 Windows 전용** (`net10.0-windows`). Linux 에서는 **컴파일만** 되고 실행 검증 불가 — COM 관련 변경은 Windows 실기 검증이 필요하다.
- 패키지 버전은 **중앙 관리**(`Directory.Packages.props`, `ManagePackageVersionsCentrally=true`) — 개별 csproj 에 버전 쓰지 말고 `Directory.Packages.props` 에서 관리.
- `InvariantGlobalization=true` (단일 exe 크기↓). 그래서 로컬라이제이션은 위성 어셈블리/`CurrentUICulture` 대신 **명시적 언어코드 + 임베드 JSON** 을 쓴다(아래 참조).

## 아키텍처 (여러 파일에 걸친 큰 그림)

**핵심 루프 — `src/MoaiCode.Core/Agent/QueryEngine.cs`**: 모델 스트리밍 → 권한 게이트(`IPermissionGate`) → 툴 디스패치 → 결과 주입 → 재진입(멀티턴). 컨텍스트가 창의 ~70% 를 넘으면 선제 컴팩션하고, 원래 사용자 요청(`_goal`)을 재고정(re-anchor)해 표류를 막는다. mid-stream 끊김 재시도·max_turns 연장 로직도 여기.
- `Core` 는 `Config` 를 참조할 수 없다(순환). 그래서 진단 로그는 **CLI 가 `MoaiLog.Info` 를 `Action<string>` 으로 주입**한다.

**프론트엔드별 툴 셋이 다르다 — 이게 보안 경계다:**
- 툴 원장은 `MoaiCode.Tools/ToolRegistry.cs` 의 `BuiltIn`.
- **CLI**(`MoaiCode.Cli/AppBootstrap.cs`): `BuiltIn` + **`BashTool`** (전체 권한).

**모델 계층 — `MoaiCode.Providers`**: `ProviderFactory.CreateDefault()` 가 `IChatModel` 을 만들고, `RetryingChatModel` 데코레이터가 pre-yield(첫 이벤트 전) 끊김을 지수 백오프 재시도한다. `EchoChatModel` 은 키 없을 때 폴백.

**WebSearch 는 Gemini grounding** (`MoaiCode.Tools/Web/WebSearchTool.cs`): `GEMINI_API_KEY`(또는 `zaiCode auth set gemini`) 가 없으면 그 툴만 비활성되고 나머지는 정상 동작. **`StreamJsonRunner`** (`MoaiCode.Cli`) 는 claude CLI 호환 `--output-format stream-json` / `--input-format stream-json` (NDJSON 영속 모드, `-p` 값을 없이) 프로토콜을 구현한다.

**로컬 청킹 파이프라인(문서 검색)**: 로컬 청킹(`MoaiCode.Tools.OpenXml` 의 `ChunkBuild`/`TextChunker`) + 로컬 벡터(`.moai-chunks/*.vec` float32 + `vectors.json`, 포맷은 `docs/CHUNK_VEC_FORMAT.md`) + 오프라인 코사인 검색(`ChunkSearch`). **CLI 는 임베딩하지 않는다** — 질의 벡터는 외부 파이프라인이 만들어 넘긴다(z.ai Coding Plan 에 임베딩 모델이 없다).

**로컬라이제이션 — `MoaiCode.Localization`**: **기본(base) 언어는 영어다.** 원칙: 소스 코드의 사용자 노출 문자열 리터럴은 **반드시 영어로 작성**하고(하드코딩 한글 금지), **한국어는 사용자를 위한 번역 레이어**로 `Resources/ko.json` 에 둔다. `en.json` 이 base(진실의 원천), `ko.json` 은 그 번역. 접근은 `L10n.Get(key, args)` (`string.Format(InvariantCulture)`), 임베드 `Resources/en.json`+`ko.json`. 키 규칙 `cli.*`/`slash.*`/`common.*` — **문장을 키로 쓰지 않는다**. 언어 결정: 설정(`language`) → `MOAI_LANGUAGE` env → **기본 `en`**, 폴백 `en`→키. **한국어 사용자는 `language: ko`(설정) 또는 `MOAI_LANGUAGE=ko` 로 명시**한다(한국어권 배포본은 이 설정을 포함). 규칙 상세: `docs/LOCALIZATION.md`. **en/ko 키는 항상 완전 일치시킬 것.** (로그는 이와 별개로 항상 영어 ASCII — 아래 로깅 섹션.) **영어 UI 라벨은 첫 글자 대문자(sentence case)** — 예: "Selected", "No such rule". 단 **명령어 미러(`install:`/`uninstall:`), 프로그램 프리픽스(`zaiCode:`), 괄호 상태(`(none)`), 기술 토큰/경로(`.mcp.json`)** 는 소문자 유지.

**설정 병합 순서**(뒤가 우선): `~/.claude/settings.json` → `~/.zaicode/settings.json` → `<workspace>/.claude/settings.json` → 환경변수. 자격증명은 `~/.zaicode/credentials.json`(Unix `0600`, `zaiCode auth set` 으로 기록). 자격증명 파일을 **직접 읽지 말 것** — `FileCredentialStore` 를 경유.

## 배포

- **CLI**: `./publish-win.sh` / `./publish-linux.sh` / `./publish-all.sh` → self-contained 단일 파일(`dist/<rid>/zaiCode`) + SHA-256.

## 로깅 (Logging)
- **로그 출력은 반드시 영어(ASCII)로만 작성한다.** 한글 등 비-ASCII 문자를 로그 메시지에 넣지 말 것.
  - 이유: Windows 기본 코드페이지(CP949) 등 환경에서 로그 파일/콘솔이 깨지는 것을 방지.
  - 적용 대상: `MoaiLog`를 포함한 모든 로그 메시지, 예외 컨텍스트 문자열, 진단 출력.
  - 사용자 입력(요청 텍스트 등)은 비-ASCII일 수 있으므로 **원문을 로그에 넣지 말고** 길이·해시 등 ASCII 메타데이터로 대체한다.
  - 공용 로거는 `MoaiCode.Config.MoaiLog` (파일: `~/.zaicode/logs/zaicode.log`, 레벨은 `~/.zaicode/settings.json`의 `logLevel`).
