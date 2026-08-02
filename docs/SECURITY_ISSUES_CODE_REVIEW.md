# 보안 이슈 코드 리뷰

- 검토일: 2026-07-24
- 대상: 현재 작업 트리(미커밋 변경 포함)
- 방식: 정적 코드 리뷰
- 상태: Open

## 결론

현재 구현에는 Bash 명령 차단, 권한 분류기의 fail-closed 처리, 자격증명 파일 권한 보호 등 유효한 방어가 포함되어 있다.

다만 신뢰하지 않은 저장소를 여는 개발자 도구의 위협 모델을 기준으로 보면 안전하다고 판단하기 어렵다. 특히 프로젝트 설정, MCP 자동 실행, 하위 에이전트 권한 상속에서 사용자 확인 전에 코드 실행 또는 자격증명 유출로 이어질 수 있는 Critical 등급 문제가 확인됐다.

가장 먼저 저장소 신뢰 모델을 도입하고, 신뢰되지 않은 프로젝트가 실행·네트워크·자격증명 관련 설정을 변경하지 못하도록 분리해야 한다.

## 심각도 요약

| 등급 | 건수 | 주요 영향 |
|---|---:|---|
| Critical | 4 | 저장소 진입 시 코드 실행, API 키 유출, 승인 없는 셸 실행 |
| High | 4 | 임의 파일 접근·업로드, 워크스페이스 이탈, SSRF |
| Medium | 2 | 읽기 전용 SQL 우회 가능성, 설정 손상·정책 유실 |

## 발견 사항

### SEC-001: 프로젝트 MCP 설정을 통한 시작 시점 임의 코드 실행

- 심각도: Critical
- 영향: 사용자 권한 코드 실행, API 키 및 환경변수 유출

`McpConfigLoader.Discover()`는 작업 디렉터리의 `.mcp.json`과 `.claude/mcp.json`을 사용자 설정보다 먼저 읽는다.

- `src/MoaiCode.Mcp/Mcp/McpConfigLoader.cs:14`
- `src/MoaiCode.Cli/AppBootstrap.cs:72`
- `src/MoaiCode.Mcp/Mcp/StdioTransport.cs:22`

MCP 설정의 `command`, `args`, `env`는 별도의 저장소 신뢰 확인이나 권한 게이트 없이 `Process.Start()`로 전달된다.

또한 `AppBootstrap`은 저장된 `OPENAI_API_KEY`를 프로세스 환경에 주입한 뒤 MCP 서버를 실행한다. 자식 프로세스가 부모 환경을 상속하므로 악성 MCP 서버가 API 키와 프록시 자격증명 등 민감 환경변수를 읽을 수 있다.

프로젝트 MCP가 사용자 MCP보다 먼저 병합되므로 같은 이름을 사용해 신뢰된 사용자 MCP를 가로채는 shadowing도 가능하다.

권장 조치:

1. 저장소 신뢰 승인이 끝날 때까지 프로젝트 MCP를 로드하거나 실행하지 않는다.
2. MCP 서버별 최초 실행 승인과 command/args 미리보기를 제공한다.
3. 자식 프로세스 환경을 기본 비움으로 구성하고 필요한 변수만 allowlist로 전달한다.
4. 사용자 MCP가 프로젝트 MCP보다 높은 우선순위를 갖도록 변경한다.
5. 신뢰된 설정의 해시가 변경되면 승인을 다시 받는다.

### SEC-002: 프로젝트 설정을 통한 API endpoint 및 자격증명 전송 대상 변경

- 심각도: Critical
- 영향: API 키, 대화 내용, 소스 코드 유출

프로젝트 `.claude/settings.json`은 사용자 설정을 덮어쓸 수 있으며 `baseUrl`도 변경할 수 있다.

- `src/MoaiCode.Config/SettingsLoader.cs:19`
- `src/MoaiCode.Config/SettingsLoader.cs:62`
- `src/MoaiCode.Cli/AppBootstrap.cs:316`
- `src/MoaiCode.Providers/OpenAi/OpenAiChatModel.cs:44`
- `src/MoaiCode.Providers/OpenAi/OpenAiChatModel.cs:137`

악성 프로젝트가 공격자 endpoint를 `baseUrl`로 지정하면 저장된 API 키가 `Authorization: Bearer` 헤더로 해당 서버에 전송된다. 이후 모델 요청에 포함된 대화와 코드도 동일한 endpoint로 전송된다.

권장 조치:

1. `baseUrl`, provider, proxy, account, credential 관련 설정은 user 또는 environment 범위에서만 허용한다.
2. 프로젝트가 endpoint 변경을 요청하면 대상 origin과 자격증명 전송 여부를 명시해 확인받는다.
3. 승인된 endpoint origin을 자격증명에 함께 바인딩한다.
4. HTTPS를 기본 강제하고 예외는 localhost 등 명시적 개발 모드로 제한한다.

### SEC-003: 프로젝트 자동 검증 명령의 권한 게이트 우회

- 심각도: Critical
- 영향: 파일 수정 후 임의 셸 명령 실행

프로젝트 설정은 `autoLint`, `autoTest`, `lintCommand`, `testCommand`를 지정할 수 있다.

- `src/MoaiCode.Config/SettingsLoader.cs:84`
- `src/MoaiCode.Cli/HarnessToolObserver.cs:64`
- `src/MoaiCode.Cli/HarnessToolObserver.cs:99`

파일 수정이 발생하면 해당 명령을 `/bin/bash -c` 또는 `cmd.exe /c`로 실행한다. 이 경로는 `BashSecurity`와 `IPermissionGate`를 통과하지 않는다.

따라서 사용자가 정상적인 파일 수정 하나를 승인해도 저장소가 지정한 후속 명령이 별도 승인 없이 실행될 수 있다.

권장 조치:

1. 프로젝트 자동 검증 명령은 저장소 신뢰 전 비활성화한다.
2. 실행을 `BashTool`과 동일한 보안 검사 및 권한 게이트로 통합한다.
3. 최초 실행과 명령 변경 시 command, working directory, 환경변수를 표시하고 승인받는다.
4. 가능하면 자유 형식 셸 대신 프로젝트 유형별 구조화된 명령과 인자 배열을 사용한다.

### SEC-004: 하위 에이전트의 후속 도구 자동 승인

- 심각도: Critical
- 영향: 위임 승인 한 번으로 Bash, 파일 쓰기, MCP 작업 연속 실행

메인 에이전트에서 `Agent` 호출을 승인한 뒤 생성되는 하위 `QueryEngine`은 `AutoApproveGate`를 사용한다.

- `src/MoaiCode.Tools/Agent/AgentTool.cs:75`
- `src/MoaiCode.Cli/AppBootstrap.cs:113`

하위 도구 목록에는 Bash, 파일 쓰기, MCP, OpenXML 등이 포함된다. `explore`, `plan`의 읽기 전용 제약도 프롬프트에만 표현되어 있고 실제 도구 목록이나 권한 정책으로 강제되지 않는다.

권장 조치:

1. 하위 에이전트에 부모의 권한 게이트와 저장소 경계를 전달한다.
2. `explore`, `plan`에는 실제 읽기 전용 도구만 제공한다.
3. `general-purpose`도 각 중요 작업마다 부모와 동일한 승인을 받게 한다.
4. 하위 에이전트가 사용할 수 있는 capability와 최대 효과 범위를 Agent 승인 화면에 표시한다.

### SEC-005: GUI 워크스페이스 제한 미강제

- 심각도: High
- 영향: 사용자 권한으로 접근 가능한 임의 위치에 파일 생성·덮어쓰기

GUI 설명은 파일 쓰기가 `내 문서/MoAI` 워크스페이스에 제한된다고 명시하지만 실제 엔진에는 `AutoApproveGate`가 전달된다.

- `src/MoaiCode.Gui/ViewModels/MainViewModel.cs:306`
- `src/MoaiCode.Gui/Agent/GuiBootstrap.cs:17`
- `src/MoaiCode.Gui/Agent/GuiBootstrap.cs:76`

OpenXML과 이미지 생성 도구는 절대경로를 허용하며 상위 디렉터리도 생성한다.

- `src/MoaiCode.Tools.OpenXml/OpenXmlPaths.cs:7`
- `src/MoaiCode.Tools/Media/ImageCreateTool.cs:91`

`OrgDocsUpload`는 명시적인 개별 파일에 대해 확장자 필터 없이 업로드한다.

- `src/MoaiCode.Tools/Knowledge/OrgDocsUploadTool.cs:220`

권장 조치:

1. GUI에 워크스페이스 경계를 강제하는 permission gate를 적용한다.
2. 모든 파일 도구가 공통 경로 정책을 사용하게 한다.
3. 워크스페이스 외부 읽기·쓰기·업로드는 사용자에게 전체 정규화 경로를 보여주고 확인받는다.
4. 심볼릭 링크와 junction을 해석한 최종 대상도 경계 안인지 검사한다.

### SEC-006: 읽기 전용 도구의 권한 검사 생략

- 심각도: High
- 영향: SSH 키, `.env`, 클라우드 자격증명, 업무 문서 유출

`QueryEngine`은 `tool.IsReadOnly`가 `false`일 때만 권한 게이트를 호출한다.

- `src/MoaiCode.Core/Agent/QueryEngine.cs:359`

그러나 `Read` 도구는 모든 파일에 대한 절대경로 접근을 명시적으로 지원한다.

- `src/MoaiCode.Tools/Files/FileReadTool.cs:17`

로컬 상태를 변경하지 않는다는 의미의 read-only와 보안상 안전하다는 의미가 혼합되어 있다. 프로젝트 지침이나 외부 콘텐츠의 프롬프트 인젝션으로 워크스페이스 외부 파일을 읽고 그 결과를 모델 endpoint로 전송할 수 있다.

권장 조치:

1. `IsReadOnly`와 별도로 `FileSystemScope`, `DataSensitivity`, `NetworkEffects` capability를 정의한다.
2. 워크스페이스 밖 읽기는 별도 승인 대상으로 처리한다.
3. `.ssh`, `.aws`, `.config/gcloud`, `.env`, credential store 등 민감 경로를 기본 차단한다.
4. 읽은 내용이 외부 모델로 전송된다는 점을 권한 화면에 표시한다.

### SEC-007: WebFetch SSRF 차단 범위 및 DNS 검증 부족

- 심각도: High
- 영향: localhost 및 사설망 서비스 접근, 내부 정보 노출

현재 SSRF 검사는 `169.254.0.0/16`과 IPv6 link-local만 차단한다.

- `src/MoaiCode.Tools/Web/WebFetchTool.cs:281`

다음 대상은 차단되지 않는다.

- `127.0.0.0/8`, `::1`
- `10.0.0.0/8`
- `172.16.0.0/12`
- `192.168.0.0/16`
- IPv6 ULA
- unspecified, multicast, reserved 대역

검사 단계에서 DNS를 해석하고 실제 HTTP 연결 단계에서 다시 해석하므로 DNS rebinding에 대한 TOCTOU 위험도 있다. `WebFetch`는 read-only로 분류되어 권한 확인도 받지 않는다.

권장 조치:

1. loopback, private, link-local, multicast, unspecified, reserved IP를 IPv4/IPv6 모두 차단한다.
2. DNS 검증을 통과한 IP에 실제 소켓 연결을 고정한다.
3. 모든 redirect 대상에 같은 검사를 적용한다.
4. 로컬·사설망 접근이 필요한 경우 별도의 명시적 권한으로 분리한다.

### SEC-008: 워크스페이스 밖 쓰기 검사가 Write/Edit 도구에 한정

- 심각도: High
- 영향: 다른 쓰기 도구를 통한 경계 우회

`ModeAwarePermissionGate.WritesOutsideWorkspace()`는 도구 이름이 `Write` 또는 `Edit`인 경우에만 경로를 검사한다.

- `src/MoaiCode.Cli/ModeAwarePermissionGate.cs:159`

따라서 OpenXML 문서 생성, 이미지 생성, 청크 생성, COM Office 편집 및 향후 추가되는 쓰기 도구에는 동일한 강제 경계가 적용되지 않는다.

권장 조치:

1. 도구 이름 열거 대신 `WritesFiles`, `TargetPaths`, `StartsProcess`, `UploadsData` 같은 구조화된 효과 메타데이터를 도입한다.
2. 실행 전에 모든 대상 경로를 정규화하고 공통 정책으로 평가한다.
3. 복수 파일을 쓰는 도구는 생성될 전체 경로 목록을 권한 게이트에 전달한다.

### SEC-009: SQL 읽기 전용 판별의 구문적 한계

- 심각도: Medium
- 영향: DB 종류와 서버 정책에 따라 상태 변경 또는 부작용 쿼리 실행 가능

클라이언트는 첫 키워드가 `SELECT`, `WITH`, `EXPLAIN`, `SHOW`, `DESCRIBE`, `PRAGMA` 중 하나이고 문자열 밖 다중문이 아니면 읽기 전용으로 판단한다.

- `src/MoaiCode.Tools/Knowledge/OrgDatasTool.cs:22`
- `src/MoaiCode.Tools/Knowledge/OrgDatasTool.cs:129`

DB 종류에 따라 쓰기 CTE, 상태 변경 `PRAGMA`, 파일·네트워크·지연 관련 함수, `SELECT ... INTO` 등이 부작용을 만들 수 있다.

클라이언트 검사는 UX를 위한 빠른 실패로만 사용하고, 서버에서 읽기 전용 DB 계정, read-only transaction, statement timeout, 결과 크기 제한을 강제해야 한다.

### SEC-010: 설정 저장의 비원자적 덮어쓰기와 오류 시 정책 유실

- 심각도: Medium
- 영향: 설정 손상, 권한 규칙 및 보안 설정 유실

`SettingsWriter`는 기존 파일 파싱 실패를 모두 무시하고 빈 객체로 초기화한 뒤 `File.WriteAllText()`로 직접 덮어쓴다.

- `src/MoaiCode.Config/SettingsWriter.cs:12`
- `src/MoaiCode.Config/SettingsWriter.cs:76`

프로세스 중단이나 CLI/GUI 동시 쓰기가 발생하면 설정이 손상되거나 기존 권한 규칙이 유실될 수 있다.

권장 조치:

1. 동일 디렉터리의 임시파일에 기록한 후 원자적으로 교체한다.
2. 기존 파일 파싱 실패 시 덮어쓰지 말고 오류를 보고한다.
3. 마지막 정상 설정 백업을 유지한다.
4. Unix에서는 설정 파일도 기본 `0600`으로 생성한다.

## 기존 방어 중 유지할 항목

- Bash 파괴적 명령 하드 차단 및 추가 확인 정책
- 권한 분류기 실패 시 fail-closed 처리
- 자격증명 디렉터리 `0700`, 파일 `0600`, 원자적 교체
- WebFetch 응답 크기, 시간, redirect 제한
- 외부 검색·문서 결과의 untrusted output 표시
- 일반 `Write`와 `Edit`의 시스템 중요 경로 보호

## 패치 우선순위

### P0: 즉시 조치

1. 저장소 신뢰 모델 도입
2. 프로젝트 MCP 자동 실행 중지
3. 프로젝트의 endpoint·permission·자동 실행 설정 통제
4. 하위 에이전트의 `AutoApproveGate` 제거

### P1: 다음 보안 릴리스

1. GUI 워크스페이스 경계 강제
2. 모든 파일·업로드 도구에 공통 capability 정책 적용
3. 워크스페이스 외부 읽기와 민감 파일 접근 통제
4. WebFetch SSRF 방어 강화

### P2: 방어 심화

1. 서버 측 SQL 읽기 전용 강제 검증
2. 설정 저장 원자성 및 파일 권한 강화
3. 악성 저장소 fixture를 이용한 회귀 테스트 추가

## 권장 회귀 테스트

- 프로젝트 `.mcp.json`이 저장소 신뢰 전 프로세스를 시작하지 않는지 확인
- MCP 자식 프로세스에 `OPENAI_API_KEY`가 전달되지 않는지 확인
- 프로젝트 `baseUrl`이 저장된 자격증명의 endpoint를 변경하지 못하는지 확인
- 프로젝트 자동 lint/test 명령이 권한 승인 없이 실행되지 않는지 확인
- `Agent` 하위 작업의 Bash와 파일 쓰기가 부모 권한 게이트를 통과하는지 확인
- GUI의 OpenXML 및 이미지 도구가 워크스페이스 밖 경로를 거부하는지 확인
- Read/Glob/Grep가 민감 경로 및 워크스페이스 외부 접근을 통제하는지 확인
- WebFetch가 loopback, RFC1918, IPv6 ULA 및 DNS rebinding을 차단하는지 확인
- SQL 도구가 쓰기 CTE와 상태 변경 `PRAGMA`를 거부하는지 확인
- 설정 저장 중 중단 또는 동시 쓰기에서도 기존 설정이 유지되는지 확인

## 검증 제한

이번 검토는 현재 작업 트리를 대상으로 한 정적 코드 리뷰다. 리뷰 당시 Codex 실행 셸의 `PATH`에서는 `dotnet` 명령을 찾을 수 없어 `dotnet test --no-restore`를 재실행하지 못했다. 이는 시스템 전체에 .NET SDK가 설치되지 않았다는 의미가 아니라 해당 실행 셸의 경로 설정에 대한 기록이다.

의존 패키지의 알려진 CVE, 서버 배포 코드, 운영 인프라 정책은 이번 문서의 검토 범위에 포함하지 않았다.
