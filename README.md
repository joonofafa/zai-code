# MoAI Code — C# 포트 (cs_conv)

MoAI Code — OpenClaude(TypeScript 코딩 에이전트 CLI)를 기반으로 한 C# 포트.
전체 변환 계획은 상위 디렉토리의 [`CSHARP_PORT_PLAN.md`](../CSHARP_PORT_PLAN.md) 참고.

## 설계 목표

- **Windows 11에서 런타임 설치 없이 단일 exe로 구동** — self-contained 단일 파일 게시.
- .NET 10 타깃. 빌드 머신은 Linux/macOS/Windows 무관(크로스 게시 지원).
- UI는 Spectre.Console 기반 MVU(React/Ink 대체).

## 진행 현황 (요약)

Phase 0의 walking skeleton을 넘어 **Phase 5~7의 대부분 기능이 구현된 상태**입니다.
세부는 [`PROGRESS.md`](PROGRESS.md) 참고.

| Phase | 내용 | 상태 |
|---|---|---|
| 0 | 11-프로젝트 솔루션 골격, CPM, 빌드/게시 파이프라인 | ✅ |
| 1 | OpenAI 호환 SSE 스트리밍, `RetryingChatModel`, 컨텍스트 오버플로 복구 | ✅ |
| 2 | `QueryEngine` 멀티턴, 툴 디스패치, `IPermissionGate` | ✅ |
| 3 | Read/Write/Edit(+Read→Write 가드), Glob/Grep, WebFetch/WebSearch | ✅ |
| 4 | BashTool + `BashSecurity` deny-list | ✅ |
| 5 | 세션/히스토리/체크포인트/사용량 스토어, 3-tier 설정, 자격증명 파일 스토어 | ✅ |
| 6 | MCP(stdio) 클라이언트·툴 어댑팅, 스킬·플러그인 로더, 번들 스킬 추출 | ✅ |
| 7 | Spectre TUI MVU, 슬래시 커맨드, 컴팩션(선제/강제), goal 리앵커, `AgentTool`, Task 툴 | ✅ |
| 8 | System.CommandLine 서브커맨드, 헤드리스 러너, 사내망 프록시, 엔터프라이즈 로그인/MFA | ✅ |
| ⏭ | Anthropic/Gemini 네이티브, OS 키링 백엔드, 다국어 예고형 감지 확장 | ⏳ |

## 솔루션 구조

```
MoaiCode.Core         도메인 모델, 에이전트 루프(QueryEngine), 툴/모델 계약, 컴팩션·리앵커
MoaiCode.Providers    IChatModel 구현(OpenAI 호환 SSE, Echo, RetryingChatModel)
MoaiCode.Tools        Read/Write/Edit/Glob/Grep/WebFetch/WebSearch/Task/Agent
MoaiCode.Tools.Bash   BashTool + BashSecurity
MoaiCode.Mcp          MCP stdio 클라이언트, SkillLoader, PluginLoader, BundledSkills
MoaiCode.Config       Settings(3-tier merge), FileCredentialStore, ProxyConfig
MoaiCode.Persistence  SessionStore, HistoryStore, CheckpointStore, UsageStore
MoaiCode.Tui          Spectre.Console MVU, Slash, LineEditor
MoaiCode.Cli          엔트리, AppBootstrap, LoginFlow, ProxyFlow, HeadlessRunner
MoaiCode.Sdk          공개 SDK 타입
tests/MoaiCode.Core.Tests   xUnit
```

## 사전 요구사항 (개발/빌드 머신만)

- .NET 10 SDK ([dotnet.microsoft.com](https://dotnet.microsoft.com/download))
- **최종 사용자(Windows 11)는 아무것도 설치할 필요 없음** — 게시된 exe에 런타임 포함.

## 빠른 시작

```bash
dotnet build
dotnet test

# REPL
dotnet run --project src/MoaiCode.Cli

# 헤드리스
dotnet run --project src/MoaiCode.Cli -- run "요약: README를 3줄로"

# 툴/스킬 목록
dotnet run --project src/MoaiCode.Cli -- tools
dotnet run --project src/MoaiCode.Cli -- skills

# API 키 저장 (~/.moai/credentials.json, unix 0600)
dotnet run --project src/MoaiCode.Cli -- auth set OPENAI_API_KEY sk-...

# 엔터프라이즈 로그인 (호스트는 MOAI_LOGIN_HOST 환경변수로 오버라이드 가능)
dotnet run --project src/MoaiCode.Cli -- login
dotnet run --project src/MoaiCode.Cli -- logout

# 사내망 프록시
dotnet run --project src/MoaiCode.Cli -- proxy http://proxy.corp:8080 --user alice
dotnet run --project src/MoaiCode.Cli -- proxy --clear
```

## Windows용 단일 exe 게시 (런타임 불필요)

Linux/macOS 또는 Windows 어디서든:

```bash
./publish-win.sh          # → dist/win-x64/moai.exe
./publish-linux.sh        # → dist/linux-x64/moai
# 또는 Windows PowerShell: ./publish-win.ps1
```

`dist/win-x64/moai.exe` 하나만 Windows 11로 복사하면 실행됩니다.
ARM64 Windows는 `./publish-win.sh win-arm64`.

## 설정

- `~/.moai/settings.json` (사용자) → `<repo>/.moai/settings.json` (프로젝트) → 환경변수 순으로 머지.
- 주요 키: `Model`, `BaseUrl`, `Permission`(Ask/Auto/Deny), `MaxTurns`,
  `ContextWindowTokens`, `ConfineToWorkspace`, `RepoMapTokens`, `OutputStyle`,
  `Account`, `Host`, `LoginAt`.
- 자격증명: `~/.moai/credentials.json` (파일 기반. unix 0600. OS 키체인 백엔드는 로드맵).

## MCP

- 프로젝트 루트의 `.mcp.json`을 감지하여 stdio 트랜스포트로 서버를 spawn.
- 원격 툴은 `McpTool`로 어댑팅되어 내장 툴과 동일하게 권한 게이트를 통과.

## 슬래시 커맨드 (REPL)

`/clear`, `/resume`, `/model`, `/cost`, `/usage`, `/tools`, `/skills`, `/help` 등.

## 알려진 제약 / 로드맵

- 프로바이더 라우팅: 현재 OpenAI 호환만. Anthropic·Gemini 네이티브 경로는 미구현.
- 자격증명은 파일 기반. Windows Credential Manager / macOS Keychain / libsecret 백엔드 예정.
- `LooksLikeAnnouncedAction`(예고형 감지)은 한/영 특화. 다국어 확장 예정.
- 엔터프라이즈 로그인 호스트는 `MOAI_LOGIN_HOST` 환경변수 또는 `login <host>` 인자로 지정.
  (지정하지 않으면 소스 코드의 컴파일 시 기본값이 사용됨.)
