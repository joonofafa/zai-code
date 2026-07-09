# 진행 현황 (자율 세션)

날짜: 2026-06-26 · 환경: Linux 개발 머신(.NET 10.0.301 SDK는 `~/.dotnet`에 user-local 설치)

## 한 줄 요약

MoAI Code(OpenClaude 기반) C# 포트. **Phase 0~5 완료 + Phase 6~8 상당 부분 구현**
(MCP·스킬·플러그인·서브에이전트·Task·헤드리스·`auth`/`login`/`proxy` 서브커맨드·
엔터프라이즈 로그인/MFA·컴팩션·goal 리앵커).
**11개 프로젝트 솔루션**(Core/Providers/Tools/Tools.Bash/Mcp/Config/Persistence/Tui/Cli/Sdk/Tests)이
빌드되고 xUnit 스위트가 통과하며, **런타임 미설치 환경에서 단일 바이너리 실행**도 확인함.

## 완료된 작업

| Phase | 내용 | 상태 |
|---|---|---|
| 0 | 10-프로젝트 솔루션 골격, DI 준비, CPM, 빌드/게시 파이프라인 | ✅ |
| 1 | OpenAI 호환 프로바이더 + SSE 스트리밍(`SseReader`, `OpenAiChatModel`), 툴콜 청크 누적 | ✅ |
| 2 | 에이전트 루프 툴 디스패치 + 멀티턴(`QueryEngine`) | ✅ |
| 3 | 코어 파일 툴: Read/Write/Edit/Glob/Grep | ✅ |
| 4 | BashTool(CliWrap) + 단순화 보안(`BashSecurity` deny-list) | ✅ |
| 5(부분) | 세션 JSONL 영속화(`SessionStore`, 다형성 직렬화) + 슬래시 명령(/help /tools /clear /exit) | ✅ |
| 1(하드닝) | 프로바이더 재시도(`RetryingChatModel`, transient 429/5xx 백오프) + 에러 분류(`ErrorCategory`) | ✅ |
| 6 | MCP 클라이언트(JSON-RPC over stdio, `IMcpTransport`/`McpClient`/`McpManager`/`McpTool`) + `.mcp.json` 로더 + 스킬 로딩(`SkillLoader`/`SkillTool`) | ✅ |
| 7 | 본격 TUI: stateful `QueryEngine` + `IPermissionGate`, Spectre.Live 증분 렌더, 대화형 권한 다이얼로그(`SpectrePermissionGate`: 허용/항상허용/거부), 비대화형 평문 폴백 | ✅ |
| 8 | System.CommandLine 2.0.9 서브커맨드(`run`/`tools`/`skills`/`mcp list`/`auth` + 기본 REPL), `AppBootstrap` 공유, `HeadlessRunner`, 슬래시 레지스트리 | ✅ |
| 5+ | 설정 머지(`SettingsLoader` 3-tier JSONC) + 자격증명(`ICredentialStore`/`FileCredentialStore`) + 입력 히스토리(`HistoryStore`) + `/history` + `auth` 서브커맨드 + 프로바이더 오류 우아한 처리 | ✅ |
| 6+ | 플러그인 로딩(`PluginLoader`) + 서브에이전트(`AgentTool`) + Task 툴(`TaskStore`/TaskCreate/List/Update) | ✅ |

## 검증 결과 (직접 실행)

- `dotnet build` → 0 경고, 0 오류
- `dotnet test` → **62/62 통과** (SSE/툴콜/파일툴/디스패치/Bash보안/세션/재시도/MCP/스킬/권한게이트/슬래시명령/설정머지/자격증명/히스토리/Agent/Task/플러그인)
- **MCP end-to-end 실프로세스 검증**: 파이썬 stdio MCP 서버에 연결 → `mcp__py__ping` 툴 발견, `greet` 스킬 로딩 확인
- **TUI 검증**: PTY에서 Spectre.Live 패널이 토큰 단위 증분 렌더 확인 / 파이프(비대화형)에서는 평문 스트리밍 폴백 확인
- **서브커맨드 검증**: `--version`/`--help`/`run "..."`(헤드리스)/`tools`/`skills`/`mcp list` 동작 확인
- `dist/linux-x64/moai`를 `env -i`(깨끗한 환경, PATH에 dotnet 없음)로 실행 → 정상 구동 확인
- `dist/win-x64/moai.exe` (40MB, PE32+ Windows 단일 exe, .NET 런타임 내장) 생성 — **Windows 11에서 설치 없이 실행 가능**

## 핵심 설계 (요약)

- **.NET 10 LTS** + self-contained 단일 파일 게시 (Native AOT 대신 — 추후 동적 플러그인/스킬 로딩의 reflection 대비)
- async generator → `IAsyncEnumerable`, AbortSignal → `CancellationToken`
- Zod → **JSON Schema 단일 소스**(LLM 전송 + 런타임 검증 공유)
- React/Ink → Spectre.Console **MVU**(`Model`/`Update` 순수 함수) + 대화형은 `Spectre.Live` 증분 렌더, 비대화형은 평문 폴백
- BashTool 보안: 풀 AST 파싱 대신 고위험 패턴 deny-list + 권한 모드 위임("불확실하면 보수적")

## 실제 LLM 연결 방법

```bash
export OPENAI_API_KEY=sk-...
export OPENAI_BASE_URL=https://api.openai.com/v1   # 또는 ollama/deepseek 등 호환 엔드포인트
export MOAI_MODEL=gpt-4o-mini
dotnet run --project src/MoaiCode.Cli              # 대화형 REPL
dotnet run --project src/MoaiCode.Cli -- run "이 폴더 요약해줘"   # 헤드리스 1회 실행
```
키가 없으면 오프라인 `EchoChatModel`로 폴백.

## 서브커맨드 / 슬래시 명령

- CLI: `moai` (REPL) · `run "<prompt>" [-m model]` · `tools` · `skills` · `mcp list` · `auth set <provider> <key>` · `auth list` · `--version` · `--help`
- REPL 슬래시: `/help /tools /model /skills /mcp /cost /history /sessions /save <name> /resume <name> /clear /exit`
- 설정: `~/.claude/settings.json` → `./.claude/settings.json` → env 순 머지 (keys: model, provider, baseUrl, permission, maxTurns)
- 자격증명: `openclaude auth set openai <key>` → `~/.openclaude/credentials.json` (env 없을 때 자동 주입)

## 다음 단계 (미착수)

- LSP(gRPC) 툴 — 규모가 커서 후순위
- Phase 7 나머지: 뷰포트 스크롤/히스토리 화살표 네비게이션, 마크다운 렌더, 입력 멀티라인/붙여넣기
- 프로바이더 확장: Anthropic 네이티브, Gemini, reasoning 포맷 변형
- 플러그인 명령/MCP 기여(현재는 스킬만), 자격증명 OS 키체인 백엔드

전체 로드맵: 상위 `../CSHARP_PORT_PLAN.md`.
