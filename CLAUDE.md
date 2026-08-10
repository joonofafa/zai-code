# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

# moai-code 프로젝트 지침

Claude Code(OpenClaude 계열 에이전트)를 C#/.NET 10 으로 포팅한 코딩·문서 에이전트. CLI(`moai`) + TUI + Avalonia 데스크톱 GUI(MoAI Desktop) 세 프론트엔드가 동일한 코어(`QueryEngine`)를 공유한다. OpenAI 호환 SSE 스트리밍 모델을 사용한다.

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
dotnet run --project src/MoaiCode.Gui                          # 데스크톱 UI (Windows 전용 타깃)
```

- 빌드가 hang 하면 VBCSCompiler 데드락 — `-p:UseSharedCompilation=false -nodeReuse:false` 로 우회.
- API 키가 없으면 `EchoChatModel` 로 폴백(오프라인 개발 가능). 실제 모델은 `OPENAI_API_KEY` + `OPENAI_BASE_URL` (+ `MOAI_MODEL`) 환경변수.

## 멀티타깃 & 플랫폼 (자주 걸리는 부분)

- `Directory.Build.props` 가 모든 프로젝트에 `net10.0` 을 강제하되 **`MoaiCode.Cli` 만 예외**(멀티타깃 `net10.0;net10.0-windows`). props 는 프로젝트 본문보다 먼저 평가되므로 프로젝트 이름으로 분기한다.
- **Office COM 툴(`MoaiCode.Tools.Office`)은 Windows 전용** (`net10.0-windows`). Linux 에서는 **컴파일만** 되고 실행 검증 불가 — COM 관련 변경은 Windows 실기 검증이 필요하다.
- **`MoaiCode.Gui` 는 `net10.0-windows` 단일 타깃 + `WinExe`** (Avalonia 지만 Office COM 참조 때문). Linux 에서 GUI 빌드는 되지만 실행은 Windows.
- 패키지 버전은 **중앙 관리**(`Directory.Packages.props`, `ManagePackageVersionsCentrally=true`) — 개별 csproj 에 버전 쓰지 말고 `Directory.Packages.props` 에서 관리.
- `InvariantGlobalization=true` (단일 exe 크기↓). 그래서 로컬라이제이션은 위성 어셈블리/`CurrentUICulture` 대신 **명시적 언어코드 + 임베드 JSON** 을 쓴다(아래 참조).

## 아키텍처 (여러 파일에 걸친 큰 그림)

**핵심 루프 — `src/MoaiCode.Core/Agent/QueryEngine.cs`**: 모델 스트리밍 → 권한 게이트(`IPermissionGate`) → 툴 디스패치 → 결과 주입 → 재진입(멀티턴). 컨텍스트가 창의 ~70% 를 넘으면 선제 컴팩션하고, 원래 사용자 요청(`_goal`)을 재고정(re-anchor)해 표류를 막는다. mid-stream 끊김 재시도·max_turns 연장 로직도 여기.
- `Core` 는 `Config` 를 참조할 수 없다(순환). 그래서 진단 로그는 **CLI 가 `MoaiLog.Info` 를 `Action<string>` 으로 주입**한다.

**프론트엔드별 툴 셋이 다르다 — 이게 보안 경계다:**
- 툴 원장은 `MoaiCode.Tools/ToolRegistry.cs` 의 `BuiltIn`.
- **CLI**(`MoaiCode.Cli/AppBootstrap.cs`): `BuiltIn` + **`BashTool`** (전체 권한).
- **GUI**(`MoaiCode.Gui/Agent/GuiBootstrap.cs` → `CuratedTools()`): 일반 사용자 안전을 위해 **Bash·조직 SQL·삭제 툴 제외**한 큐레이션 셋 + Office COM + 로컬 인덱싱 + 스킬. 파일 쓰기는 워크스페이스(`내 문서\MoAI`)로 confine.
- GUI 는 별도 데몬/IPC 없이 **동일 프로세스 in-process** 로 `QueryEngine` 을 돌린다.

**모델 계층 — `MoaiCode.Providers`**: `ProviderFactory.CreateDefault()` 가 `IChatModel` 을 만들고, `RetryingChatModel` 데코레이터가 pre-yield(첫 이벤트 전) 끊김을 지수 백오프 재시도한다. `EchoChatModel` 은 키 없을 때 폴백.

**로컬 RAG 파이프라인(문서 검색)**: 로컬 청킹(`MoaiCode.Tools.OpenXml` 의 `ChunkBuild`/`TextChunker`) + **서버 `/v1/embeddings`(compute-only, 원본 미저장)** + 로컬 벡터(`.moai-chunks/*.vec` float32 + `vectors.json`, 포맷은 `docs/CHUNK_VEC_FORMAT.md`) + 오프라인 코사인 검색. 조직 문서함(서버 저장/공유)은 별개이며 **수동 업로드만** — 폴더 자동 업로드는 제거됨.

**로컬라이제이션 — `MoaiCode.Localization`**: `L10n.Get(key, args)` (`string.Format(InvariantCulture)`), 임베드 `Resources/ko.json`+`en.json`. 키 규칙 `cli.*`/`slash.*`/`gui.<area>.*`/`common.*` — **문장을 키로 쓰지 않는다**. 언어 결정: 설정(`language`) → `MOAI_LANGUAGE` env → 기본 `ko`, 폴백 `en`→키. GUI XAML 정적 문자열은 마크업 확장 `{l10n:Loc gui.key}`(시작 언어 기준 정적 해석, 런타임 전환은 재시작), VM 동적은 `L10n.Get(...)`. 규칙 상세: `docs/LOCALIZATION.md`. **en/ko 키는 항상 완전 일치시킬 것.**

**설정 병합 순서**(뒤가 우선): `~/.claude/settings.json` → `~/.moai/settings.json` → `<workspace>/.claude/settings.json` → 환경변수. 자격증명은 `~/.moai/credentials.json`(Unix `0600`). 자격증명 파일을 **직접 읽지 말 것** — `FileCredentialStore`/aws CLI 등을 경유.

## 배포

- **CLI**: `./publish-win.sh` / `./publish-linux.sh` / `./publish-all.sh` → self-contained 단일 파일 + SHA-256.
- **GUI(MoAI Desktop)**: `./publish-gui.sh` → win-x64 단일 exe → `~/shareHub/Zone/`. **`IncludeNativeLibrariesForSelfExtract=true` 필수** — 누락 시 네이티브 라이브러리(Avalonia/SkiaSharp)가 exe 밖으로 빠져 실행 불가. 스크립트에 정상 exe(~50MB) 미만이면 배포 중단하는 가드가 있다. `publish-all.sh` 는 CLI 전용이니 GUI 에 쓰지 말 것.

## 로깅 (Logging)
- **로그 출력은 반드시 영어(ASCII)로만 작성한다.** 한글 등 비-ASCII 문자를 로그 메시지에 넣지 말 것.
  - 이유: Windows 기본 코드페이지(CP949) 등 환경에서 로그 파일/콘솔이 깨지는 것을 방지.
  - 적용 대상: `MoaiLog`를 포함한 모든 로그 메시지, 예외 컨텍스트 문자열, 진단 출력.
  - 사용자 입력(요청 텍스트 등)은 비-ASCII일 수 있으므로 **원문을 로그에 넣지 말고** 길이·해시 등 ASCII 메타데이터로 대체한다.
  - 공용 로거는 `MoaiCode.Config.MoaiLog` (파일: `~/.moai/logs/moai.log`, 레벨은 `~/.moai/settings.json`의 `logLevel`).
