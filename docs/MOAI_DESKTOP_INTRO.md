# MoAI Desktop — 소개 & '확장 프로그램 설치' 등록 가이드

> MoAI Code(CLI)와 나란히 **'확장 프로그램 설치'** 섹션에서 소개·다운로드할 수 있도록
> 준비한 문서입니다. 앞부분은 **사용자용 소개 카피/기능**(웹 페이지에 그대로 사용 가능),
> 뒷부분은 **웹/서버팀 통합 스펙**(다운로드 라우트·배포 위치)입니다.

---

## 1. 사용자용 소개

**제품명:** MoAI Desktop
**한 줄 소개:** 클릭 몇 번으로 문서를 만드는 **데스크톱 문서 도구** (일반 사용자용)

**설명:**
> MoAI Code가 개발자용 터미널 코딩 에이전트라면, **MoAI Desktop 은 CLI에 익숙하지 않은
> 일반 사용자**를 위한 문서 작업 앱입니다. 채팅으로 워드·엑셀·발표자료를 만들고,
> 내 PC나 회사 문서함의 자료를 참조로 붙여 **근거 있는 문서**를 생성합니다. 런타임 설치가
> 필요 없는 단일 실행 프로그램입니다. (현재 Windows 전용)

### 주요 기능 (FEATURES 배열용)

| 기능 | 설명 | 아이콘(제안) |
|---|---|---|
| **대화형 문서 작성** | 채팅으로 워드/엑셀/발표자료 생성 — 표·차트·이미지 포함 | `MessageSquare` |
| **참조 기반 작성** | 로컬 파일·조직 문서함을 참조로 첨부해 근거 있는 문서 생성 | `FileStack` |
| **문서함 검색·연동** | 조직 문서함을 검색해 관련 내용(스니펫) 또는 원문 통째로 참조 | `Search` |
| **AI 이미지 생성·삽입** | 프롬프트로 이미지를 만들어 문서에 바로 삽입 | `Image` |
| **폴더 동기화(트레이 상주)** | 공유 폴더를 감시해 변경 문서를 조직 문서함에 자동 반영 | `FolderSync` |
| **모던 UI · 테마** | 웹과 통일된 라이트/다크 테마, 최초 실행 시 선택 | `Palette` |
| **런타임 불필요** | .NET 설치 없이 단일 실행파일로 동작(self-contained) | `PackageCheck` |

### 참조 방식 3가지 (핵심 차별점)
- 📎 **내 로컬 파일 통째** — docx/xlsx/pptx/pdf/txt/md/csv 원문을 그대로 근거로
- 🗂 **조직 문서함 원문 통째** — 선택한 문서 원문을 근거로
- 🗂 **조직 문서함 검색 스니펫** — 질문에 관련된 부분만 근거로

---

## 2. 다운로드 & 설치 (Windows)

1. **MoAI Desktop (Windows 64bit)** 다운로드 → 압축 해제
2. `MoAI Desktop.exe` 실행 (별도 런타임 설치 불필요)
3. 최초 실행 시 **테마 선택** → 바로 사용

> 로그인은 MoAI Code와 동일한 자격증명(`~/.moai/credentials.json`)을 사용합니다.
> (별도 로그인 UI는 후속 예정 — 현재는 CLI `moai login` 자격증명 공유)

---

## 3. 웹/서버팀 통합 스펙

MoAI Desktop 은 **Avalonia 기반 Windows 데스크톱 앱**(win-x64 self-contained 단일 exe)입니다.
`/moai-code` 페이지와 동일한 패턴으로 등록하면 됩니다.

### 3.1 배포 아티팩트 (이미 스테이징 완료 ✅)
- 파일: `MoAI Desktop.exe` (win-x64, self-contained, 단일 실행 50MB)
- 압축: `moai-code-desktop-win-x64.zip` (exe 1개 포함, 약 44MB)
- 배포 위치: **이미 업로드됨** — `~/open-moai-vip/storage/moai-code/moai-code-desktop-win-x64.zip`
  (CLI zip 과 동일 규칙 `moai-code-<platform>.zip`, platform=`desktop-win-x64`)

### 3.2 다운로드 라우트 — **남은 작업 (택1)**
zip 은 이미 올라가 있으나, `app/api/moai-code/download/[platform]/route.ts` 의 `PLATFORMS`
화이트리스트에 `desktop-win-x64` 가 없어 **아직 404** 입니다. 아래 중 하나만 반영하면 열립니다:

- **(A) 플랫폼 추가(간단·권장):** `PLATFORMS` 에 `'desktop-win-x64'` 한 줄 추가 → 즉시 서빙.
  (파일·규칙 모두 이미 맞춰둠. 코드 한 줄이면 끝)
- **(B) 전용 라우트:** `/api/moai-desktop/download/win-x64` 신설(더 명확).

> ⚠️ 스토리지는 `.next/standalone` 밖(`../../storage/moai-code`)에 두어야 rsync --delete 로
> 지워지지 않습니다(CLI와 동일 규칙).

### 3.3 소개 페이지 (택1)
- **(A) `/moai-code` 페이지에 섹션 추가** — "일반 사용자용 데스크톱" 카드 + Windows 다운로드 버튼
- **(B) `/moai-desktop` 신설** — `/moai-code/page.tsx` 를 복제, 위 FEATURES/카피로 교체,
  DOWNLOAD_GROUPS 는 Windows 64bit 단일 항목

사이드바 '확장 프로그램 설치' 버튼(`components/moai-code/moai-code-install.tsx`)에서
두 제품(코드/데스크톱)을 함께 안내하거나, 링크 대상 페이지에서 분기하면 됩니다.

---

## 4. 참고 (기술)
- 코어(문서 생성·문서함 연동 툴)를 **in-process 임베드**(데몬·IPC 없음). CLI와 동일 엔진.
- 일반 사용자 안전을 위해 **Bash/파괴적 툴 제외**(큐레이트 툴셋).
- 조직 문서함 업로드/다운로드·검색은 기존 v1 API(Bearer) 사용 — 서버 추가작업 없음.
- 현재 Windows 전용(Avalonia). 크로스플랫폼 확장은 후속 검토.
