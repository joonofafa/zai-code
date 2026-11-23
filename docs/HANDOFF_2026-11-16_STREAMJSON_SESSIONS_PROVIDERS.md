# HANDOFF: stream-json 세션 복원 · mate providers/sessions 커맨드 (2026-11-16)

> 작업 인계 문서. 기준: 브랜치 `zai` (upstream/zai + 1커밋 `2927b97`, 버전 1.5.2) 위의
> **커밋되지 않은 작업 트리 변경**. 다음 세션은 이 문서와 `git diff` 를 함께 볼 것.
> (GUI 관련 `HANDOFF_*.md` 는 upstream 잔재라 이 브랜치와 무관.)

## 변경 내용 (커밋 전 작업 트리)

### 1. stream-json 고도화 — `src/MoaiCode.Cli/StreamJsonRunner.cs` (+167줄)

`RunLoopAsync` 가 `SessionStore?` 와 `resumeId?` 를 받는다:

- **세션 복원(`--resume`)**: `resumeId` 가 있으면 `SessionStore.LoadAsync` 로 복원해
  `engine.Restore(restored)` 하고 `sessionId` 를 그대로 쓴다. 없는 id 면 새 세션.
  `system/init` 이벤트에 `resumed:true` 가 붙는다.
- **replay**: 복원한 대화를 user/assistant/tool_result 이벤트 형식에 `"replay":true` 를
  붙여 다시 내보낸다(상대 화면이 이전 대화를 다시 그릴 수 있게). 시스템 메시지·추론 블록 제외.
- **턴 단위 저장**: 턴이 끝날 때마다 `sessions.SaveAsync(sessionId, engine.Messages)` —
  브리지가 끊겨도 마지막 끝난 턴까지 `--resume` 으로 잇는다.
- **`{"type":"interrupt"}` 입력 이벤트**: 진행 중인 턴만 취소(`TurnControl` 로 연결된
  CTS 취소), 대기 중 승인은 `gate.FailAllPending()` 로 거부 마감. 프로세스·대화 맥락은 유지,
  `result` 는 `stop_reason:"canceled"`, exit code 130.

### 2. `mate sessions` 서브커맨드 — `src/MoaiCode.Cli/Program.cs`

현재 폴더의 저장된 세션 목록을 TSV(id \t 수정시각 ISO \t 메시지 수 \t 제목)로 출력.
런타임/MCP 를 만들지 않고 바로 끝난다 — 브리지·웹 UI 가 자주 부르므로.
`--resume` 의 id 소스가 이 출력이다.

### 3. `mate providers` 서브커맨드 — `src/MoaiCode.Cli/ProvidersCommand.cs` (신규 214줄)

`/provider`·`/model` 이 하는 일을 대화형 화면 없이 JSON 으로:

- 인자 없음: 전체 상태(active, fixedContextWindow, 프로바이더별 model/baseUrl/
  contextWindow/keySource(stored|env)/requiresKey/usable)
- `models <id> [--base-url] [--key-stdin]`: 모델 목록(+컨텍스트 창). 저장 전 키 검증용.
- `set <id> --model … [--context-window] [--vision-model] [--base-url] [--key-stdin]`:
  저장+활성화. 설정 먼저 쓰고(깨진 settings.json 이면 키도 안 건드림) 그다음 키 저장.
- `delete-key <id>`: credentials.json 의 저장된 키만 삭제(env 키는 건드리지 않음).
- **API 키는 인자로 받지 않는다**(프로세스 목록에 남음) — `--key-stdin` 만.

### 4. 부수 변경

- `Config/CredentialStore.cs`: `FileCredentialStore.Remove(key)` 추가(delete-key 용).
- `Tui/ModelPicker.cs`: `MinContextWindow`/`MaxContextWindow` 를 `internal`→`public`
  (ProvidersCommand 검증이 씀).
- `Localization/en.json`·`ko.json`: `cli.sessions.*`·`cli.providers.*` 키 5쌍 추가.
- `Tui/BottomDock.cs` `ResizeEraseBand`: 폭 변경+높이 동일 시 유령 박스 중복(사용자 호소) 수정
  — rewrap 여유(구분선 2행×증가분, 상한 6)만큼 위로 더 지운다.
- `tests/…/BashSecurityTierTests.cs`: 테스트 데이터 경로 오타 정정(moai-code→moaiCode).
- `tests/…/BottomDockResizeTests.cs` (신규): 리사이즈 erase band 회귀 테스트 1건.

## 남은 작업 (우선순위 순)

1. **CLAUDE.md 반영** — 재작성된 CLAUDE.md(작업 트리)에 새 CLI 계약이 빠짐:
   - `sessions`/`providers` 서브커맨드 존재·용도(런타임 없이 즉시 종료)
   - `--resume` 이 stream-json 입력 모드에서 실제 복원 동작(더 이상 no-op 아님)
   - stream-json 이벤트 계약: `interrupt` 입력, `result.stop_reason:"canceled"`,
     `init.resumed:true`, `replay:true` 이벤트 — 브리지 연동 에이전트가 알아야 함
   - `mate providers` 는 키를 인자로 안 받고 `--key-stdin` 만(보안 규칙)
2. **회귀 테스트 추가** — 프로젝트 관례(디스패치·영속성 변경은 회귀 테스트) 기준:
   - `StreamJsonRunnerTests` 는 `ExtractUserText` 만 커버.
     `IsInterrupt` 파싱(유효/무효 JSON, "interrupt" 단어 포함 비-이벤트),
     resumeId 복원 경로(없는 id → 새 세션), Replay 직렬화 형식 필요.
   - `ProvidersCommand` 검증: context window 범위 밖, vLLM base-url 누락,
     `--key-stdin` 인데 stdin 비었을 때.
3. **커밋 분할 제안**: (a) BottomDock 리사이즈 fix + 테스트, (b) stream-json
   resume/interrupt + sessions 커맨드, (c) providers 커맨드 — Conventional Commit.

## 참고

- openClaude v0.36.0 의 `glm.ts` 변경(`providerModelMap`)은 OpenGateway 용이라
  Z.ai 직접 연결인 이 저장소와 무관 — 이식 불요.
- 세션 스토어 경로: `~/.mate/projects/<percent-encoded-cwd>/sessions`(CLAUDE.md 참조).
