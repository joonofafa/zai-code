# MoAI Code

OpenClaude 계열의 에이전트 실행 구조를 C#/.NET 10으로 구현한 코딩·문서 작업 에이전트입니다. OpenAI 호환 스트리밍 모델을 사용하며, 터미널 CLI/TUI와 Avalonia 데스크톱 UI를 함께 제공합니다.

현재 CLI 버전은 **1.8.5**, Desktop 버전은 **0.4.5**입니다. 구현 및 검증 상세는 [`PROGRESS.md`](PROGRESS.md)를 참고하세요.

## 주요 기능

- `QueryEngine` 기반 멀티턴 에이전트, SSE 스트리밍, 툴 호출, 컨텍스트 컴팩션 및 goal 리앵커
- Read/Write/Edit/Glob/Grep/Bash/WebFetch/WebSearch와 서브에이전트·Task 툴
- MCP stdio 서버, 로컬·플러그인·번들 스킬 로딩
- 세션·히스토리·체크포인트·사용량 저장과 대화형 권한 승인
- 조직 문서/데이터 검색·업로드·삭제 및 로컬 문서 청킹/검색
- DOCX/PPTX/XLSX 생성·검사, 생성 이미지 삽입, Windows Office COM 연동
- Avalonia 데스크톱 UI: 로그인, 채팅, 로컬/조직 문서 참조, 생성 파일 열기, 폴더 동기화
- 엔터프라이즈 로그인/MFA, HTTP(S) 프록시, 프록시 우회, reasoning effort 설정
- Windows/Linux/macOS용 self-contained 단일 CLI 바이너리 게시

## 솔루션 구조

솔루션은 제품 프로젝트 14개와 테스트 프로젝트 3개로 구성됩니다.

```text
src/
  MoaiCode.Core           에이전트 루프, 메시지·툴 계약, 권한, 컴팩션
  MoaiCode.Providers      OpenAI 호환 SSE, Echo 폴백, 재시도 계층
  MoaiCode.Tools          파일·검색·웹·조직 지식·이미지·Task·Agent 툴
  MoaiCode.Tools.Bash     Bash 실행 및 명령 보안 정책
  MoaiCode.Tools.OpenXml  DOCX/PPTX/XLSX 생성·검사, 로컬 문서 청킹
  MoaiCode.Tools.Office   Windows PowerPoint/Excel COM 연동
  MoaiCode.Mcp            MCP stdio, 스킬·플러그인 로더
  MoaiCode.Config         설정, 자격증명, 로그인·프록시 구성
  MoaiCode.Localization   ko/en 문자열 카탈로그와 런타임 언어 전환
  MoaiCode.Persistence    세션, 히스토리, 체크포인트, 사용량 저장
  MoaiCode.Tui            Spectre.Console TUI, 라인 편집기, 슬래시 명령
  MoaiCode.Cli            CLI 엔트리, 부트스트랩, 로그인, 헤드리스 실행
  MoaiCode.Gui            Avalonia 데스크톱 애플리케이션
  MoaiCode.Sdk            공개 SDK 타입
tests/
  MoaiCode.Core.Tests
  MoaiCode.OpenXml.Tests
  MoaiCode.Office.Tests
```

## 요구사항

- 개발 및 빌드: .NET 10 SDK
- Windows Office 실시간 제어: Windows와 설치된 Microsoft PowerPoint/Excel
- 게시된 self-contained CLI를 사용하는 최종 사용자에게는 .NET 런타임이 필요하지 않습니다.

`dotnet`이 시스템 `PATH`에 없고 사용자 디렉터리에 설치되어 있다면 아래 명령의 `dotnet`을 `~/.dotnet/dotnet`으로 바꾸면 됩니다.

## 빠른 시작

```bash
dotnet build MoaiCode.sln
dotnet test MoaiCode.sln

# 대화형 CLI/TUI
dotnet run --project src/MoaiCode.Cli

# 헤드리스 1회 실행
dotnet run --project src/MoaiCode.Cli -- run "이 저장소를 요약해줘"

# 데스크톱 UI
dotnet run --project src/MoaiCode.Gui
```

API 키가 없으면 `EchoChatModel`로 폴백합니다. OpenAI 호환 엔드포인트를 직접 사용할 때는 다음처럼 지정합니다.

```bash
export OPENAI_API_KEY=sk-...
export OPENAI_BASE_URL=https://api.openai.com/v1
export MOAI_MODEL=gpt-4o-mini
dotnet run --project src/MoaiCode.Cli
```

## CLI

대표 서브커맨드는 다음과 같습니다.

```bash
moai
moai run "프롬프트" --model gpt-4o-mini
moai tools
moai skills
moai mcp list
moai auth set openai sk-...
moai auth list
moai login
moai logout
moai proxy http://proxy.corp:8080 --user alice
moai proxy --clear
moai language             # 현재 언어와 지원 언어 조회
moai language en          # 영어로 변경하고 저장
```

REPL은 `/plan`, `/act`, `/model`, `/effort`, `/language`, `/permissions`, `/checkpoint`, `/restore`, `/sessions`, `/save`, `/resume`, `/usage`, `/review`, `/security-review`, `/bughunter`, `/simplify`, `/tools`, `/skills`, `/mcp`, `/help` 등을 제공합니다.

`/effort`는 현재 워크트리에서 `low`, `medium`, `high` 추론 강도를 조회·변경하며 사용자 설정에 저장합니다.

## 문서·이미지 작업

전 플랫폼에서 Open XML SDK 기반 툴을 사용할 수 있습니다.

- `DocxCreate`, `PptxCreate`, `XlsxCreate`: 새 Office 문서 생성
- `OfficeDocInspect`: 닫힌 DOCX/PPTX/XLSX 구조와 텍스트 검사
- `ImageCreate`: OpenAI 호환 `/images/generations` 호출 및 PNG 저장
- DOCX/PPTX 생성 시 로컬 이미지 또는 생성 이미지를 문서에 삽입
- `ChunkBuild`, `ChunkFetch`, `ChunkSearch`: 문서를 로컬 청크 파일로 만들고 조회·벡터 검색

Windows 빌드에서는 설치된 Office를 COM으로 제어하는 툴이 추가됩니다. 현재 PowerPoint 검사·텍스트 수정과 Excel 검사를 지원하므로, PowerPoint/Excel을 열어 둔 상태에서 CLI 명령 결과를 애플리케이션 화면에 바로 반영할 수 있습니다. Office 미설치 또는 정책 차단 환경에서는 COM 툴이 등록되지 않습니다.

## 데스크톱 UI

`MoaiCode.Gui`는 별도 데몬 없이 동일 프로세스 안에서 에이전트 런타임을 실행합니다.

- 자격증명이 없으면 로그인 창을 먼저 표시하며 프록시 주소·계정을 함께 설정할 수 있습니다.
- 로컬 파일 전체 텍스트, 조직 문서 검색 결과 또는 조직 문서 원문을 채팅 참조로 첨부할 수 있습니다.
- 생성된 DOCX/PPTX/XLSX 파일은 채팅 카드에서 바로 열 수 있습니다.
- 연결 폴더는 변경을 감지해 중복을 제거한 뒤 조직 문서 서버로 동기화합니다.
- GUI는 안전한 선별 툴 집합을 사용하며 Bash와 조직 데이터 삭제 툴은 노출하지 않습니다.

## 설정과 자격증명

설정은 뒤 항목일수록 우선하여 병합됩니다.

1. `~/.claude/settings.json`
2. `~/.moai/settings.json`
3. `<workspace>/.claude/settings.json`
4. 환경변수

주요 키는 다음과 같습니다.

- 모델: `provider`, `model`, `baseUrl`, `reasoningEffort`
- 인터페이스: `language`(`ko`/`en`, 기본 `ko`), 환경변수 `MOAI_LANGUAGE`
- 실행: `permission`(`ask`/`auto`/`deny`), `maxTurns`(기본 25), `contextWindow`, `outputStyle`
- 보안: `confineToWorkspace`, `permissions.allow`, `permissions.deny`, `checkpoints`
- 자동 검증: `lintCommand`, `testCommand`, `autoLint`, `autoTest`
- 네트워크: `proxyUrl`, `proxyUser`, `proxyBypass`
- 로그인 표시 정보: `host`, `account`, `loginAt`, `orgName`

자격증명은 기본적으로 `~/.moai/credentials.json`에 저장되며 Unix에서는 `0600` 권한을 적용합니다. OS 키체인 백엔드는 아직 제공하지 않습니다.

LLM 게이트웨이의 장시간 SSE 연결이 사내 프록시에서 끊기는 문제를 피하기 위해 `baseUrl` 호스트는 프록시 우회 목록에 자동 포함됩니다. 스트림이 첫 이벤트 전에 일시적으로 끊기면 최대 5회, 8초 상한의 지수 백오프로 재시도합니다.

## MCP와 스킬

- 워크스페이스의 `.mcp.json`을 찾아 stdio MCP 서버를 실행합니다.
- MCP 툴도 내장 툴과 동일한 권한 게이트를 통과합니다.
- 프로젝트·사용자·플러그인 스킬과 기본 번들 스킬을 로딩하며, 같은 이름이면 사용자 스킬을 우선합니다.

## 언어

CLI/TUI/GUI는 `MoaiCode.Localization`의 동일한 임베드 JSON 카탈로그를 사용합니다. 현재 한국어와 영어를 지원하며, `language` 설정·`MOAI_LANGUAGE`·`moai language`·`/language`·GUI 설정에서 선택할 수 있습니다. 새 언어 추가와 문자열 이관 규칙은 [`docs/LOCALIZATION.md`](docs/LOCALIZATION.md)를 참고하세요.

## 배포

CLI 프로젝트는 `win-x64`, `win-x86`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64` RID를 선언합니다.

```bash
./publish-win.sh             # dist/win-x64/moai.exe
./publish-win.sh win-arm64
./publish-linux.sh           # dist/linux-x64/moai
./publish-all.sh             # 지원 RID 일괄 게시
# Windows PowerShell: ./publish-win.ps1
```

게시 결과는 self-contained 단일 파일이며 스크립트가 SHA-256 체크섬을 함께 생성합니다.

## 알려진 제약

- 모델 프로바이더는 현재 OpenAI 호환 API 중심입니다. Anthropic·Gemini 네이티브 어댑터는 미구현입니다.
- 조직 문서/데이터와 이미지 생성은 구성된 서버 API 및 해당 기능 지원 여부에 의존합니다.
- 로컬 `ChunkSearch`의 의미 검색은 문서 벡터와 질의 벡터가 준비되어 있어야 합니다.
- Office COM은 Windows 전용이며 현재 편집 범위가 제한적입니다. 닫힌 파일의 생성·검사는 Open XML 툴을 사용합니다.
- 운영 배포 전 문서 쓰기의 원자성, Office COM 취소/개체 식별, 엄격한 XLSX 주소 검증 등 추가 하드닝이 필요합니다.
- 자격증명은 파일 기반이며 Windows Credential Manager, macOS Keychain, libsecret 연동은 로드맵 항목입니다.
