# MoAI Docs — Avalonia GUI 설계 (일반 사용자용 문서 작업툴)

> 목표: **CLI에 익숙하지 않은 일반 사용자**가 채팅하듯 요청하면 **문서(docx/xlsx/pptx)** 를 만들고,
> 회사 문서함에 올리고, 로컬 문서를 검색하는 **Windows 데스크톱 앱**. moai-code의 .NET 코어를
> **in-process 임베드**(데몬·IPC 없음). Avalonia(.NET) 사용 — Windows 대상, 코드베이스는 크로스플랫폼 여지.

---

## 1. 설계 원칙

1. **터미널/CLI 은닉**: 명령어·플래그·경로·권한 용어 노출 안 함. "무엇을 만들어 드릴까요?" 수준.
2. **문서 중심**: 핵심 동선은 "요청 → 문서 생성 → 미리보기/열기 → (선택) 문서함 업로드".
3. **안전 기본값**: 일반 사용자에겐 **위험 도구(임의 셸)를 아예 노출하지 않는다**(툴 큐레이션). 권한
   프롬프트는 최소화하고, 뜨더라도 평이한 말로.
4. **코어 재사용**: 엔진·툴·세션·인증을 CLI와 공유. GUI는 또 하나의 "프론트엔드"일 뿐.

---

## 2. 아키텍처 (임베드)

```
┌───────────────────────────── MoaiCode.Gui (Avalonia, net10.0-windows) ─────────────────────────────┐
│  View(XAML)  ─▶  ViewModel(MVVM)  ─▶  GuiAgentSession                                              │
│      ▲                  ▲                    │  engine.SubmitAsync(prompt) → IAsyncEnumerable<Event> │
│      │ Dispatcher       │ ObservableCollection│  (TextDelta / ToolCallRequested / ToolExecuted / …)  │
│      └──────────────────┴────────────────────┘                                                     │
│                              │ in-process 참조                                                      │
└──────────────────────────────┼─────────────────────────────────────────────────────────────────────┘
                               ▼
   MoaiCode.Core (QueryEngine, StreamEvent, IPermissionGate)  ·  MoaiCode.Tools(.OpenXml/.Knowledge)
                               │
                     IChatModel → open-moai (vip.bccard.ai)
```

- 데몬/소켓/JSON-RPC **없음**. Avalonia 앱이 `MoaiCode.Core`/`MoaiCode.Tools*`/`MoaiCode.Config`를 직접 참조.
- CLI(`MoaiCode.Cli`)의 조립 로직(AppBootstrap)을 GUI용으로 재사용/이식.

---

## 3. 솔루션 구성

```
src/
  MoaiCode.Core/            (그대로) 엔진·이벤트·권한 추상화
  MoaiCode.Tools*/          (그대로) 문서/지식/청킹 툴
  MoaiCode.Config/          (그대로) 설정·인증(credentials.json)
  MoaiCode.Cli/             (그대로) 터미널 프론트엔드
  MoaiCode.Gui/   ← 신규     Avalonia 프론트엔드
    App.axaml / Program.cs
    Bootstrap/GuiBootstrap.cs      (AppBootstrap 이식 — GUI용 게이트/툴셋)
    Agent/GuiAgentSession.cs       (SubmitAsync 소비 + 이벤트→VM 매핑)
    Agent/GuiPermissionGate.cs     (IPermissionGate → 모달 다이얼로그)
    ViewModels/ Views/ Controls/
```

`MoaiCode.Gui.csproj`: `TargetFramework=net10.0-windows`, `OutputType=WinExe`, Avalonia 패키지,
`ProjectReference` = Core/Tools/Tools.OpenXml/Tools.Knowledge/Config (Bash 미참조 — 아래 큐레이션).

---

## 4. 코어 연동 상세

### 4.1 이벤트 스트림 소비 (엔진 → UI)

기존 `QueryEngine.SubmitAsync(prompt, ct) : IAsyncEnumerable<StreamEvent>` 를 그대로 사용
(HeadlessRunner와 동일 소비, 출력만 UI로).

```csharp
public async Task SendAsync(string prompt)
{
    IsBusy = true;
    var run = new StringBuilder();
    await foreach (var ev in _engine.SubmitAsync(prompt, _cts.Token))
    {
        switch (ev)
        {
            case TextDelta d:            run.Append(d.Text);                       break; // 스트리밍 답변
            case ToolCallRequested t:    Post(() => AddActivity(Friendly(t.Block)));break; // "엑셀 문서 만드는 중…"
            case ToolExecuted x:         Post(() => CompleteActivity(x));           break; // ✓/✗ + 산출 파일 감지
            case TurnCompleted:          Post(() => Commit(run.ToString()));        break; // 답변 확정
        }
    }
    IsBusy = false;
}
// UI 스레드 마샬링
private static void Post(Action a) => Avalonia.Threading.Dispatcher.UIThread.Post(a);
```

- 산출 파일 감지: `ToolExecuted`의 ToolName이 `DocxCreate/XlsxCreate/PptxCreate`이고 Output에 경로가
  있으면 "문서 카드"(열기/폴더/업로드)로 렌더. (툴 Output을 파싱하거나, GUI가 워크스페이스 파일 변경을
  감시.)
- `TextDelta`는 `ThinkFilter.Strip` 적용(추론 마커 제거) — HeadlessRunner와 동일.

### 4.2 권한 게이트 = 모달 다이얼로그 (실행중 왕복)

`IPermissionGate`가 이미 플러그블. GUI 구현은 UI 스레드에서 다이얼로그를 띄우고
`TaskCompletionSource`로 클릭을 기다린다(엔진은 그동안 await로 정지).

```csharp
public sealed class GuiPermissionGate : IPermissionGate
{
    public async ValueTask<bool> AllowAsync(ITool tool, ToolUseBlock call, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>();
        Dispatcher.UIThread.Post(async () =>
            tcs.SetResult(await PermissionDialog.ShowAsync(Friendly(tool, call)))); // 평이한 문구
        return await tcs.Task;
    }
}
```

- 일반 사용자용 문구: "이 파일을 회사 문서함에 올릴까요? — report.docx" [올리기] [취소] (허용/거부 대신).
- "항상 허용"은 CLI와 동일하게 규칙 저장소(settings.json)로 영속 — 재질문 감소.

### 4.3 스레딩/취소

- 엔진 루프는 백그라운드 Task. 모든 UI 갱신은 `Dispatcher.UIThread`.
- "중지" 버튼 → `CancellationTokenSource.Cancel()` (엔진 SubmitAsync 취소).

---

## 5. 툴 큐레이션 (일반 사용자 안전 서브셋)

GUI 엔진에는 **문서/지식 작업에 필요한 툴만** 등록하고 **임의 셸(Bash)·파괴적 도구는 제외**한다.
이것만으로 권한 위험·프롬프트가 대부분 사라진다.

| 포함 | 목적 |
|---|---|
| DocxCreate / XlsxCreate / PptxCreate | 문서 생성(표·차트·도형 포함) |
| OfficeDocInspect | 생성물 점검 |
| ChunkBuild / ChunkFetch / ChunkSearch | 로컬 폴더 문서 청킹·검색 |
| OrgDocs / OrgDocsList / OrgDocsUpload / OrgDocsDelete / OrgList | 회사 문서함 |
| WebSearch / WebFetch | 자료 조사(선택) |
| FileRead / (워크스페이스 한정) FileWrite/FileEdit | 미리보기·재편집 |
| **제외** Bash, OrgDatas(SQL) 등 | 일반 사용자에 불필요·위험 |

- 파일 쓰기는 **지정 워크스페이스(예: 내 문서\MoAI)** 로 제한(confine) → 워크스페이스 밖 쓰기는 차단/확인.

---

## 6. 권한 모델 (일반 사용자 친화)

- 문서 생성·워크스페이스 내 저장 → **자동 허용**(무프롬프트).
- 회사 문서함 **업로드/삭제** → 친절한 확인 1회(외부 전송·되돌릴 수 없음).
- 워크스페이스 밖 쓰기·기타 → 확인. 위험(파괴적)은 v1.7.0 크로스플랫폼 가드가 그대로 보호.
- Bash 미포함이므로 "셸 실행 허용?" 류 프롬프트 자체가 없음.

---

## 7. UX 화면 설계

```
┌───────────────────────────────────────────────────────────────────────────┐
│ MoAI Docs                                             [로그인:홍길동] [설정]│
├──────────────┬────────────────────────────────────────────────────────────┤
│ 이전 작업     │  💬 대화                                                    │
│ • 카페 매출.. │   나: 2025 카페 매출 TOP10 표+차트 엑셀 만들어줘             │
│ • 거버넌스..  │   MoAI: 만들었어요 ▼                                        │
│ • 메달리온..  │   ┌ 📄 카페매출_2025.xlsx  [열기] [폴더] [문서함 올리기] ┐  │
│              │   └───────────────────────────────────────────────────┘   │
│ [+ 새 작업]   │                                                            │
├──────────────┴────────────────────────────────────────────────────────────┤
│ 빠른 작업: [📝 보고서] [📊 표·차트 엑셀] [📑 발표자료] [🔎 문서함 검색]      │
│ ┌───────────────────────────────────────────────────────────────────────┐ │
│ │ 무엇을 만들어 드릴까요?                                         [보내기]│ │
│ └───────────────────────────────────────────────────────────────────────┘ │
└───────────────────────────────────────────────────────────────────────────┘
```

- **좌**: 이전 작업(SessionStore 재사용, v1.5.8 제목 개선 그대로).
- **중앙**: 채팅 + **문서 카드**(생성 산출물). 카드 = 파일명 + [열기][폴더][문서함 올리기].
- **하단**: 빠른작업 버튼(프롬프트 프리셋) + 입력창.
- 진행 표시: "엑셀 문서 만드는 중…" 같은 활동 배지(`ToolCallRequested` 매핑). 터미널 로그 노출 없음.

---

## 8. 문서 미리보기 / 열기

- **MVP**: **[열기]** = `Process.Start(파일)` → Word/Excel/PowerPoint(설치돼 있으면)로 네이티브 오픈.
  일반 사용자에게 가장 자연스럽고 기대되는 동작.
- **Phase 2 썸네일**: Windows Shell 썸네일 API(`IShellItemImageFactory`)로 Office 문서 미리보기
  이미지 생성(설치된 핸들러 사용) → 카드에 미리보기. (LibreOffice 번들은 무겁게 가지 않음.)

---

## 9. 인증 · 설정 · 세션

- **인증**: `credentials.json`(OPENAI_BASE_URL/KEY) 재사용. 첫 실행 = 친절한 로그인 화면
  (서버·아이디/키 또는 사내 SSO). `moai login` 로직을 GUI 폼으로.
- **설정**: 워크스페이스 폴더, 기본 공개범위, 모델 선택(간단히).
- **세션**: SessionStore 그대로. "이전 작업" = /resume의 GUI판.

---

## 10. 패키징 · 배포

- Avalonia → **self-contained 단일 exe**(win-x64), 기존 publish 파이프라인과 동일 방식.
- 배포: 고객 다운로드(vip.bccard.ai `storage/moai-code/` 또는 별도 `moai-docs-*.zip`)로 동봉.
- 서명: 외부 배포는 Windows EV/Azure Trusted Signing 권장(경고 회피).

---

## 11. 단계별 로드맵

- **MVP (2~3주)**: 프로젝트 스캐폴드 · 로그인 · 채팅(이벤트 스트림) · 문서 생성 카드 · [열기] ·
  문서함 업로드 · 이전작업. 툴 큐레이션 + 워크스페이스 confine. 권한 모달.
- **Phase 2**: Shell 썸네일 미리보기 · 빠른작업 프리셋 확장 · 로컬 청킹/검색 UI · 드래그&드롭 업로드.
- **Phase 3**: 인라인 재편집(문서 카드에서 "제목 바꿔줘") · 템플릿 갤러리 · 다국어.

---

## 12. 결정 필요 사항

1. GUI 프레임워크 세부: **Avalonia MVVM(CommunityToolkit.Mvvm)** 확정?
2. 워크스페이스 기본 경로(예: `%USERPROFILE%\Documents\MoAI`).
3. 문서함 업로드를 기본 노출할지(사내 배포 대상에 따라).
4. 인증 방식(키 입력 vs SSO) — 서버팀과 협의.
5. 배포 채널/서명 주체.
