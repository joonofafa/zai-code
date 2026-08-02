# MoAI Code 진행 현황

최종 코드 대조: **2026-07-23** · CLI 버전: **1.8.5** · Desktop 버전: **0.4.5** · 개발 기준: **.NET SDK 10.0.301** (`~/.dotnet/dotnet`)

## 현재 상태

기존 C# 포트의 Phase 0~8을 넘어 조직 지식, Office/Open XML, 로컬 문서 청킹, 이미지 생성, Avalonia 데스크톱 UI까지 확장되었습니다. 현재 솔루션은 **제품 14개 + 테스트 3개, 총 17개 프로젝트**입니다.

현재 워크트리에는 별도 `MoaiCode.Localization` 프로젝트, `ko/en` 카탈로그, CLI/TUI/GUI 언어 선택 기반이 포함되어 있습니다. 아직 모든 사용자 문자열을 이관한 상태는 아닙니다.

## 구현 현황

| 영역 | 구현 내용 | 상태 |
|---|---|---|
| 에이전트 런타임 | 멀티턴 `QueryEngine`, 툴 디스패치, 스트리밍, 최대 턴 제어 | 완료 |
| 컨텍스트 | 토큰 추정, 선제 컴팩션, 복구 리마인더, goal 리앵커, 저장/재개 | 완료 |
| 프로바이더 | OpenAI 호환 Chat Completions SSE, Echo 폴백, 모델 전환 | 완료 |
| 네트워크 복원력 | 429/5xx/연결·`ResponseEnded` 분류, 첫 출력 전 최대 5회 재시도, 8초 백오프 상한 | 완료 |
| 코딩 툴 | Read/Write/Edit/Glob/Grep/Bash/WebFetch/WebSearch | 완료 |
| 작업 위임 | `AgentTool`, TaskCreate/List/Update, 사용자 질문 툴 | 완료 |
| 권한·보안 | ask/auto/deny, allow/deny 규칙, 워크스페이스 격리, 체크포인트, LLM 위험 분류 | 완료 |
| 영속화 | 세션 JSONL, 입력 히스토리, 사용량, Git 체크포인트 | 완료 |
| MCP·스킬 | stdio MCP, `.mcp.json`, MCP 툴 어댑터, 로컬·플러그인·번들 스킬 | 완료 |
| 조직 지식 | 문서 검색·목록·업로드·삭제, 데이터 목록·조회 | 완료 |
| 로컬 문서 검색 | DOCX/PPTX/XLSX/PDF/TXT 청킹, 청크 조회, 벡터 검색 | 완료 |
| Open XML | DOCX/PPTX/XLSX 생성·검사, DOCX/PPTX 이미지 삽입 | 완료 |
| 이미지 | OpenAI 호환 이미지 생성 API, base64 PNG 저장, GUI/문서 연계 | 완료 |
| Office COM | Windows PowerPoint 검사·텍스트 수정, Excel 검사, STA 디스패처 | 기본 범위 완료 |
| CLI/TUI | Spectre.Console 스트리밍 UI, 헤드리스 실행, 서브커맨드, 슬래시 명령 | 완료 |
| 데스크톱 | Avalonia 로그인·채팅·테마·트레이·참조·생성 파일 카드 | 완료 |
| 폴더 연동 | 변경 감시, debounce, 해시 중복 제거, 조직 문서 업로드 | 완료 |
| 로그인·프록시 | 엔터프라이즈 로그인/MFA, CLI·GUI 프록시 설정, LLM 호스트 자동 우회 | 완료 |
| 로컬라이제이션 | 공용 카탈로그, 설정·환경변수, CLI `language`, TUI `/language`, GUI 언어 선택 | 기반 완료, 문자열 이관 진행 중 |
| 배포 | 7개 RID, self-contained 단일 CLI 파일, SHA-256 생성 | 완료 |

## 릴리스 흐름

| 버전대 | 주요 변화 |
|---|---|
| 1.3.x | Phase 0~8 C# 포트, CLI/TUI, MCP·스킬·세션·권한 기반 완성 |
| 1.5.x | 조직 문서·데이터 API와 관련 지식 툴 확장 |
| 1.6.x | Office COM, Open XML 문서 생성/검사, 로컬 청킹·벡터 검색 |
| 1.7.x | 권한 규칙 고도화와 Avalonia 데스크톱 기반 도입 |
| 1.8.0 | GUI 문서 참조·폴더 동기화, 이미지 생성 및 문서 이미지 삽입 |
| 1.8.1~1.8.2 | SSE 조기 종료 재시도, 프록시 우회, 재시도 시간 범위 확대 |
| 1.8.3 | GUI 로그인 창과 CLI/GUI 프록시 입력 경로 |
| 1.8.4~1.8.5 | Desktop 안정화, 인라인 로그인·설정, 렌더링 및 프리징 개선 |
| 현재 워크트리 | `MoaiCode.Localization`, 한국어/영어 카탈로그, 언어 선택 기반 |

## 구성별 상세

### CLI/TUI

- 기본 REPL과 `run`, `tools`, `skills`, `mcp list`, `auth`, `login`, `logout`, `proxy` 서브커맨드
- 대화형 Spectre.Live 스트리밍과 비대화형 평문 출력
- `/plan`, `/act`, `/model`, `/effort`, `/language`, `/permissions`, `/checkpoint`, `/restore`, `/sessions`, `/save`, `/resume`, `/usage`, 코드 리뷰 계열 명령
- 비대화형 기본 권한은 fail-closed이며 명시적 `MOAI_YES=1` 또는 `permission=auto`에서만 자동 승인

### 데스크톱

- 별도 백그라운드 서비스 없이 인프로세스 `QueryEngine` 사용
- 자격증명이 없을 때 로그인 창을 표시하고 프록시를 함께 구성
- 로컬 파일 원문, 조직 검색 스니펫, 조직 문서 원문을 참조로 첨부
- 참조 하나당 텍스트 상한을 적용하여 프롬프트 과대화를 제한
- 생성된 Office 파일을 메시지 카드로 노출하고 기본 애플리케이션으로 열기
- 안전한 선별 툴 집합 사용: Bash 및 조직 데이터/문서 삭제 계열은 GUI에서 제외

### 문서 자동화

- Open XML 경로는 Office 설치 여부와 무관하게 닫힌 DOCX/PPTX/XLSX를 생성·검사
- Windows COM 경로는 열려 있는 PowerPoint/Excel과 상호작용하여 화면에 즉시 반영
- 현재 COM 편집은 PowerPoint 텍스트 변경 중심이며 Excel은 검사 중심
- 로컬 청킹 포맷과 검색 설계 문서: [`docs/LOCAL_CHUNKING.md`](docs/LOCAL_CHUNKING.md), [`docs/CHUNK_VEC_FORMAT.md`](docs/CHUNK_VEC_FORMAT.md)

## 검증 상태

- 2026-07-23 현재 워크트리에서 전체 솔루션 테스트 **464/464 통과**: Core 433, Open XML 19, Office 12. 실패·건너뜀은 없습니다.
- `MoaiCode.Gui` 빌드 성공. 기존 테마 nullable 경로의 `CS8604` 경고 1건이 남아 있습니다.
- `MOAI_LANGUAGE=ko/en` 환경에서 CLI 도움말 전환과 `moai language` 조회를 확인했습니다.
- self-contained CLI는 Windows/Linux/macOS 7개 RID 구성을 유지합니다. Windows Office COM의 실제 동작 검증은 Office가 설치된 Windows 환경이 필요합니다.

## 남은 작업과 위험

우선순위가 높은 후속 작업은 다음과 같습니다.

1. 문서 생성/수정 시 임시 파일 후 원자적 교체를 적용하고 부분 파일 잔존을 방지
2. PowerPoint COM 개체 식별 안정화, STA 작업의 취소·타임아웃·정리 보강
3. XLSX 셀 주소를 엄격한 A1 문법으로 검증하고 범위·수식 입력 정책 명시
4. 사용자 제공 출력 경로를 사용하는 모든 커스텀 쓰기 툴에 동일한 워크스페이스 경계 계약 적용
5. GUI 배포·설치·업데이트와 실제 사용자 환경 회귀 테스트 자동화
6. Anthropic·Gemini 네이티브 프로바이더 및 프로바이더별 reasoning 포맷 지원
7. Windows Credential Manager, macOS Keychain, libsecret 자격증명 백엔드
8. 로컬 임베딩 생성 파이프라인과 장기 메모리/검색 품질 고도화

관련 패칭 지침은 [`docs/REVIEW_PATCH_GUIDE_OFFICE_OPENXML.md`](docs/REVIEW_PATCH_GUIDE_OFFICE_OPENXML.md)에 정리되어 있습니다.

## 다음 검증 체크리스트

```bash
~/.dotnet/dotnet build MoaiCode.sln
~/.dotnet/dotnet test tests/MoaiCode.Core.Tests/MoaiCode.Core.Tests.csproj
~/.dotnet/dotnet test tests/MoaiCode.OpenXml.Tests/MoaiCode.OpenXml.Tests.csproj
~/.dotnet/dotnet test tests/MoaiCode.Office.Tests/MoaiCode.Office.Tests.csproj
```

Windows Office 설치 환경에서는 PowerPoint를 열어 둔 상태의 텍스트 변경, Excel 활성 통합문서 검사, COM 해제 후 Office 프로세스 잔존 여부를 별도로 확인해야 합니다.
