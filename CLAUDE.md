# CLAUDE.md

코드와 현재 브랜치가 최종 기준이다. 루트 `README.md`, `PROGRESS.md`, `docs/SERVER_TASK_*`, `GUI_*`, `HANDOFF_*`, `LOCALIZATION.md`에는 제거된 upstream 기능·옛 이름이 남아 있다. 이 문서는 매 세션 로드되므로 짧게 유지한다.

## 프로젝트와 아키텍처

**MATE Builder**는 OpenClaude 계열 실행 구조를 C#/.NET 10으로 옮긴 단일 코딩·문서 에이전트다. 실행 파일은 `mate`, 데이터 루트는 `~/.mate`, 내부 프로젝트·네임스페이스는 `MoaiCode.*`, 환경변수는 주로 `MOAI_*`/`ZAI_*`다. 13개 제품 프로젝트와 3개 xUnit 테스트 프로젝트, 2개 TUI 스모크 실행 프로젝트로 구성된다.

이 브랜치에서 upstream 조직 서버 연동, `Org*` 툴, `/login`, RAG/EmbeddingClient, Avalonia GUI는 **제거되었다. 다시 추가하지 않는다.** 기본 프로바이더는 Z.ai이며 OpenAI·Claude·OpenRouter·vLLM도 지원한다. 모델 연결 정보가 불완전하면 `EchoChatModel`로 오프라인 실행한다.

```text
Program.cs (System.CommandLine; 인자 없으면 REPL)
  -> AppBootstrap (설정/프로바이더/툴/MCP/스킬/영속성/프롬프트 조립)
  -> QueryEngine (모델 스트리밍 -> 권한 게이트 -> 툴 실행 -> 결과 주입 -> 반복)
  -> ReplApp(TUI) | HeadlessRunner | StreamJsonRunner
```

- `Core/Agent/QueryEngine.cs`: 대화 상태 소유, ~70% 선제 컴팩션, 원 요청 re-anchor, 스트림 재시도·턴 연장·context overflow 복구.
- `Providers`: Z.ai/OpenAI/OpenRouter/vLLM은 OpenAI 호환, Claude는 Messages API. `ProviderFactory` → `RetryingChatModel` → transport. 런타임 프로필은 `ActiveProvider.Current`에 둔다.
- `Tools/ToolRegistry.BuiltIn`: 파일·검색·웹·미디어·메모리 툴 원장. CLI가 Bash/백그라운드 셸·OpenXml·청킹·MCP·Task/Agent·Skill 툴을 추가한다. 툴 집합은 보안 경계다.
- `Tui`: Spectre.Console REPL, raw 입력·paste, 슬래시 명령, `/diff`, 테마. `Sdk`는 공개 클라이언트 계약이다.

## 명령

SDK는 `global.json`의 **10.0.100**(`rollForward: latestMajor`). `dotnet`이 PATH에 없으면 `~/.dotnet/dotnet`을 사용한다.

```bash
dotnet build MoaiCode.sln
dotnet test tests/MoaiCode.Core.Tests
dotnet test tests/MoaiCode.OpenXml.Tests
dotnet test tests/MoaiCode.Office.Tests
dotnet test tests/MoaiCode.Core.Tests --filter "FullyQualifiedName~TestName"
dotnet test MoaiCode.sln                       # 전체 솔루션
bash tests/run-dock-smoke.sh                  # TUI 레이아웃/리사이즈 스모크

dotnet run --project src/MoaiCode.Cli
dotnet run --project src/MoaiCode.Cli -- run "이 저장소 요약"
dotnet run --project src/MoaiCode.Cli -- tools
```

- **저장소 차원의 lint/formatter 설정이나 명령은 없다.** 빌드와 테스트가 게이트다. 사용자 설정 `lintCommand`는 저장소 표준이 아니다.
- 빌드가 멈추면 VBCSCompiler 회피 옵션 `-p:UseSharedCompilation=false -nodeReuse:false`를 붙인다.
- 헤드리스 텍스트는 stdout, 툴 진행은 stderr다. `run --output-format text|json|stream-json`, `-p`, `--input-format stream-json`도 지원한다.
- `--append-system-prompt <text>`(루트·`run` 모두)는 조립된 시스템 프롬프트 맨 끝에 빈 줄 하나를 두고 그대로 덧붙인다. stream-json 매 턴과 `--resume`마다 다시 적용되며 세션 저장·감사 로그에는 남지 않는다.
- Office.Tests의 STA 로직은 비Windows도 검증하지만, **실제 COM 동작은 Windows+Office에서만 검증**한다. CLI는 `Tools.Office`를 참조하지 않는다.

배포는 self-contained 단일 파일 + SHA-256 + `THIRD_PARTY_NOTICES.md`를 만든다(코드 서명 아님).

```bash
./publish-linux.sh [linux-x64]   # dock 스모크를 선행 게이트로 실행
./publish-win.sh [win-x64]       # Linux/macOS에서 Windows 크로스 게시
./publish-all.sh                 # 지원 RID 일괄
# Windows PowerShell: .\publish-win.ps1
```

## 핵심 불변조건과 보안

- **경로는 `Core/AppPaths.Combine(...)`만 사용**한다. 기본 루트 `~/.mate`, 테스트 격리는 `MOAI_HOME`(Windows에서 `HOME` 변경만으로는 부족). 시작 시 `~/.zaicode` → `~/.mate` 이관. 세션·메모리는 **git 루트가 아니라 percent-encoded cwd** 기준 `~/.mate/projects/<slug>/{sessions,memory}`다. 옛 `~/.moai` 주석은 무시한다.
- **프로바이더 해석은 `Config/ProviderResolver.cs` 한 곳**에서만 한다. 우선순위 `--model` → 저장된 `/provider`·`/model` → 환경변수 → 기본값. 키는 `ICredentialStore`, provider/baseUrl/credentials는 신뢰된 사용자 설정에서만 읽는다. 프로젝트 설정으로 키 전송 대상·sensitive/automation/allow/sandbox/offline 정책을 바꾸지 못하게 한다.
- 설정은 `~/.claude/settings.json` → `~/.mate/settings.json` → `<cwd>/.claude/settings.json` → env 순(JSONC). **allow 규칙은 `~/.mate`에서만**, deny는 모든 레이어에서 받는다. `~/.claude`/프로젝트의 model은 상속하지 않는다.
- **프로젝트 `.mcp.json`은 기본 로드하지 않는다.** `MOAI_ALLOW_PROJECT_MCP=1`의 명시적 opt-in이 필요하다. MCP 툴도 같은 권한 게이트와 파괴적 SQL/명령 확인을 거친다.
- **권한 판정은 `Cli/ModeAwarePermissionGate.cs`가 단일 지점**이다. Plan/Analysis 쓰기·실행 차단 → deny → 하드 차단 → 파괴적 명령/스크립트 내용 → allow → workspace 밖/원격 확인 → 자동 승인 시 LLM 위험 분류 순서다. 파괴적 명령은 auto/allow와 무관하게 매번 사람 확인(“항상 허용” 없음); 사람이 없으면 거부한다.
- `--ignore-confirm`은 `run`/`-p`/stream-json 배치에서만 파괴적 확인을 우회하며, deny·Plan·하드 차단은 유지된다. 확인 없이 지나간 쓰기·실행은 **감사 로그 기록 성공이 실행 전제**다. `Config/AuditLog.cs`의 승인 기록과 `Core/Tools/ToolEvents.Completed` 결과 기록을 유지하고 비밀·개인정보는 마스킹한다. 각 줄의 `prev`는 앞 줄 원문의 SHA-256이며 독점 잠금 안에서 읽고 이어 쓴다(`mate audit verify`가 중간 수정·삭제·삽입을 짚는다). 끝을 잘라 내거나 전체를 다시 계산한 것은 이 파일만으로 알 수 없다.
- 셸 분해는 `Core/Tools/ShellCommand.cs`, 하드/파괴/원격 판정은 `Tools.Bash/BashSecurity.cs`, rm은 `Rm.cs`를 **공유**한다. 복합 명령은 모든 세그먼트가 allow 또는 신뢰된 read-only일 때만 자동 허용한다. 명령치환·리다이렉션이 있으면 완화하지 않는다. 새 보안 정규식은 선형 시간인지 확인한다(과거 backtracking hang). 분류가 불확실하면 보수적으로 확인한다.
- 사람이 직접 승인하지 않은 Bash는 Linux bubblewrap 격리(`Tools.Bash/Sandbox.cs` + `Core/Tools/SandboxPolicy.cs`)에서 실행한다. `/` read-only, workspace·허용 cache·세션 `/tmp`만 쓰기, `/dev` 최소화, `/run`·PID/IPC 제한, **네트워크는 기본 공유**하므로 원격 DB/클러스터는 별도 게이트가 필요하다(`sandboxNetwork: false`면 `--unshare-net`으로 끊고, Probe가 그것까지 시험한다). `sandbox=auto|required|off`, `dangerously_disable_sandbox`는 사람 확인. 게이트와 BashTool의 ToolUseId 기반 unsandboxed 전달을 깨지 말 것. 셸 cwd는 유지되지만 env/function은 유지되지 않고 명령 exit code를 보존한다.
- 중앙 정책 파일(`SettingsLoader.ManagedSettingsPath`: `/etc/mate/managed-settings.json`, Windows `%ProgramData%\MATE Builder`)은 모든 레이어와 env 뒤에 적용되어 적힌 키가 우선한다. 경로를 env로 바꾸는 길을 만들지 않는다. 중앙 전용 키는 `managed` 인자로만 읽는다(`disableIgnoreConfirm`).
- 툴 호출: 필수 인자가 빠지면 `ToolArguments.Violation`이 실행 전에 이름을 짚어 돌려준다(조용히 고치지 않는다). `disabledTools`는 하위 에이전트 포함 툴 목록에서 뺀다.
- 맥락: 70% 임계에서 먼저 오래된 툴 결과를 표식으로 비우고(`ClearOldToolResults`), 80% 아래로 내려가지 않을 때만 요약한다. `testGate`는 `IToolObserver.BeforeStopAsync`로 종료를 막는다(요청당 2회).
- `offline`/`MOAI_OFFLINE=1`은 internet 툴을 `IConditionalTool`로 숨기고 실행도 거부한다. 대화 모델과 Bash 네트워크까지 끄는 것은 아니다. 새 조건부 툴은 모델에 노출되기 전 필터링해야 한다.
- `/diff`의 턴별 기록은 Edit/Write가 `ToolContext.Changes`에 남기는 `Core/Diff/FileChangeJournal`이다. 서브에이전트는 부모 journal을 공유하고 reset으로 지우지 않는다. Bash로 바꾼 파일은 턴 기록에 없다.

## 코딩·테스트 관례

- C#: 4 spaces, nullable/implicit usings, PascalCase public/type, camelCase local/parameter, `I` interface. 기존 구조와 helper를 먼저 재사용하고 병렬 구현을 만들지 않는다.
- **Core는 Config를 참조할 수 없다**(순환). 진단 로그는 `Action<string>` 주입. 원자 파일 쓰기는 `Core/AtomicFile`, 공용 HTTP는 `Core/Web/ToolHttp`, 경로 검증은 `PathSafety`를 재사용한다. 새 툴은 적절한 assembly + registry/bootstrap + 권한/보안 회귀 테스트를 함께 갱신한다.
- 패키지 버전은 `Directory.Packages.props`에만 둔다. 대부분 `net10.0`; CLI만 `net10.0;net10.0-windows`, Office/Office.Tests는 `net10.0-windows`. 이미지 처리는 `StbImageSharp` + 자체 `Raster`(ImageSharp를 되돌리지 않음). 새 entry point는 출력 전 `ConsoleSetup.EnsureUtf8()`을 호출한다.
- UI 사용자 문자열은 영어 원문 + `L10n.Get("cli.*"/"slash.*"/"common.*", args)`. `Resources/en.json`과 `ko.json`의 키를 정확히 맞추고 문장을 키로 쓰지 않는다. 기본 언어는 **en**, invariant globalization이므로 embedded JSON 사용. **로그는 항상 영어 ASCII**, 사용자 입력 원문·비밀 대신 길이/hash 등 메타데이터만 남긴다.
- xUnit `[Fact]`/`[Theory]`, 테스트명 `MethodName_condition_expectedResult`, 해당 동작의 테스트 프로젝트에 둔다. 보안·디스패치·영속성·localization·문서 변경은 회귀 테스트를 추가한다. 환경변수/콘솔 전역 상태 테스트는 `[Collection("EnvMutating")]`로 직렬화하고 `MOAI_HOME`으로 격리한다.
- 번들 스킬 원본은 `Cli/Resources/skills-src/`, 실행 리소스는 `bundled-skills.zip`. 내용을 바꾸면 zip과 `BundledSkills.Version`도 갱신한다. `ChunkSearch`는 외부에서 만든 같은 모델의 vector를 받으며 CLI가 embedding하지 않는다.
- 커밋은 집중된 Conventional Commit 제목(`feat(cli):`, `fix(tui):`, `docs:` 등). 자격증명·로컬 설정·`bin/obj/dist/publish`를 커밋하지 않는다.

## 디렉터리

```text
src/
  MoaiCode.Core          QueryEngine, model/tool 계약, prompt, memory, AppPaths, sandbox/셸 분해, diff
  MoaiCode.Providers     OpenAI-compatible/Claude transport, retry/switch/model directory, Echo fallback
  MoaiCode.Tools         BuiltIn 파일·검색·웹·미디어·메모리, Agent/Task 공통 툴
  MoaiCode.Tools.Bash    Bash, background shell, destructive/remote 판정, bubblewrap
  MoaiCode.Tools.OpenXml DOCX/XLSX/PPTX 생성·편집·검사, PDF/Office 읽기, chunk/vector 검색
  MoaiCode.Tools.Office  Windows-only Office COM; CLI 미참조
  MoaiCode.Mcp           stdio MCP, config discovery, skill/plugin loader
  MoaiCode.Config        settings merge, provider resolution, credentials, permissions, audit/log/proxy
  MoaiCode.Localization  L10n + embedded en/ko JSON
  MoaiCode.Persistence   session/history/checkpoint/usage stores
  MoaiCode.Tui           REPL, editor/dock, pickers, slash commands, themes, diff UI
  MoaiCode.Cli           executable entry/bootstrap/runners/permission gate/bundled skills
  MoaiCode.Sdk           public client API
tests/
  MoaiCode.Core.Tests    대부분의 unit/integration/security tests
  MoaiCode.OpenXml.Tests 문서 round-trip/layout/injection tests
  MoaiCode.Office.Tests  STA contracts + Windows/Office COM validation
  dock-smoke/ dock-resize-smoke/ run-dock-smoke.sh
demo/                    RewardCalculator.cs 샘플
docs/                    chunk vector 형식 등; upstream 문서 혼재
```
