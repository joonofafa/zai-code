# Office/Open XML 변경 코드 취약 사항 패칭 가이드

> 작성일: 2026-07-21  
> 대상: moai-code v1.6.5~v1.6.9에서 추가·확장된 Office COM, Open XML 문서 생성, 로컬 청킹 기능  
> 목적: 코드 리뷰에서 확인된 경로 경계, 문서 오인 편집, 비원자적 저장, XLSX 범위 검증,
> COM 취소·수명 관리 문제를 안전하게 수정한다.

---

## 1. 패칭 범위와 우선순위

| 우선순위 | 문제 | 주요 영향 |
|---|---|---|
| P0 | 커스텀 쓰기 툴이 `ConfineToWorkspace` 경계를 우회 | 워크스페이스 밖 파일 생성·덮어쓰기 |
| P0 | `PowerPointEdit`가 검사했던 문서가 아닌 현재 활성 문서를 수정 | 다른 프레젠테이션 오인 편집 |
| P1 | DOCX/PPTX/XLSX를 최종 경로에 직접 작성 | 실패 시 기존 문서 훼손·부분 파일 잔존 |
| P1 | XLSX 차트 A1 범위 검증 부족 | 예외, 잘못된 캐시, 손상된 차트 |
| P1 | STA 큐가 취소를 무시하고 런타임에서 해제되지 않음 | 취소 후 지연 편집, 스레드·COM 자원 누수 |
| P2 | 실제 Office 상호운용 회귀 테스트 부족 | OpenXmlValidator 통과 후 Office에서 복구·오렌더링 |

권장 적용 순서:

1. 공통 쓰기 대상 계약과 권한 게이트 순서 수정
2. 문서 생성의 원자적 저장 적용
3. XLSX A1 범위 파서와 교차 검증 추가
4. PowerPoint 문서·슬라이드 식별 계약 강화
5. 취소 가능한 STA dispatcher와 런타임 수명 관리
6. 회귀·수용 테스트 보강

---

## 2. P0 — 워크스페이스 쓰기 경계 통합

### 2.1 현재 문제

`ModeAwarePermissionGate.WritesOutsideWorkspace`는 툴 이름이 `Write` 또는 `Edit`일 때만 `path`를
검사한다. 다음 쓰기 툴은 같은 경계를 적용받지 않는다.

- `DocxCreate`
- `XlsxCreate`
- `PptxCreate`
- `ChunkBuild` (`path`가 가리키는 파일·폴더 옆에 `.moai-chunks` 생성)
- 이후 추가될 문서 변환·내보내기 툴

또한 영속 allow/deny 규칙이 워크스페이스 경계보다 먼저 평가된다. 따라서 `Write` 또는 문서 생성 툴의
allow 규칙이 있으면 외부 경로 확인도 건너뛸 수 있다.

### 2.2 수정 원칙

- 툴 이름을 하드코딩해 쓰기 경로를 추론하지 않는다.
- 로컬 파일을 변경하는 툴이 자신의 쓰기 대상을 선언하게 한다.
- 워크스페이스 경계 검사는 영속 allow 규칙보다 먼저 수행한다.
- `ConfineToWorkspace=true`에서 외부 쓰기는 대화형이면 매 호출 확인, 비대화형이면 거부한다.
- 사용자가 특정 툴을 “항상 허용”했더라도 워크스페이스 밖 쓰기 확인은 생략하지 않는다.
- 각 툴 내부에서도 경로 정규화와 확장자 검사를 유지한다(심층 방어).

### 2.3 공통 계약 추가

`MoaiCode.Core/Tools/IWorkspaceWriteTool.cs`:

```csharp
using System.Text.Json;

namespace MoaiCode.Core.Tools;

/// <summary>로컬 파일시스템에 쓸 경로를 권한 게이트에 공개하는 툴.</summary>
public interface IWorkspaceWriteTool
{
    IReadOnlyList<string> GetWriteTargets(JsonElement input);
}
```

문서 생성 툴 예시:

```csharp
public sealed class XlsxCreateTool : ITool, IWorkspaceWriteTool
{
    public IReadOnlyList<string> GetWriteTargets(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.Object
            && input.TryGetProperty("path", out var value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } path)
        {
            return new[] { path };
        }

        return Array.Empty<string>();
    }
}
```

`ChunkBuild`는 입력 파일이 아니라 실제 생성될 사이드카 경로를 반환하는 것이 이상적이다.

```csharp
// 입력이 파일이면 부모/.moai-chunks, 디렉터리면 path/.moai-chunks
public IReadOnlyList<string> GetWriteTargets(JsonElement input)
```

경로가 아직 존재하지 않더라도 문자열 기준으로 부모를 계산할 수 있어야 한다. 해석 실패 시 빈 목록으로
조용히 통과시키지 말고 권한 게이트 또는 툴 실행에서 오류 처리한다.

### 2.4 권한 게이트 순서 수정

`ModeAwarePermissionGate.AllowAsync`의 순서는 다음이어야 한다.

```text
1. Plan 모드 쓰기 차단
2. 워크스페이스 밖 쓰기 확인                 ← 영속 규칙보다 먼저
3. 파괴적 하드 차단(BashTool 실행 시에도 재검사)
4. 영속 deny
5. 영속 allow
6. 원격·파괴적 확인 티어
7. LLM 위험 분류
8. 기본 권한 모드
```

예시:

```csharp
if (_state.Mode == AgentMode.Plan && !tool.IsReadOnly)
{
    return false;
}

if (_confine && WritesOutsideWorkspace(tool, call))
{
    return await ConfirmAsync(tool, call, ct).ConfigureAwait(false);
}

switch (_rules?.Evaluate(tool, call))
{
    // deny / allow
}
```

공통 경로 검사:

```csharp
private bool WritesOutsideWorkspace(ITool tool, ToolUseBlock call)
{
    if (tool is not IWorkspaceWriteTool writer)
    {
        return false;
    }

    return writer.GetWriteTargets(call.Input)
        .Any(path => PathSafety.IsOutsideWorkspace(_workspace, path));
}
```

기존 `Write`·`Edit`도 이 인터페이스를 구현하여 이름 기반 특례를 제거한다.

### 2.5 테스트

다음 회귀 테스트를 `PermissionScopeTests` 또는 별도 테스트 파일에 추가한다.

- `XlsxCreate(path="../outside.xlsx")` + `ConfineToWorkspace=true` → confirmer 호출
- `PptxCreate` 절대 외부 경로 → confirmer 호출
- `ChunkBuild` 외부 디렉터리 → confirmer 호출
- 비대화형 confirmer 없음 → 외부 쓰기 거부
- `permissions.allow=["XlsxCreate"]`가 있어도 외부 경로는 확인
- 같은 allow 규칙에서 워크스페이스 내부 경로는 추가 확인 없이 허용
- `Plan` 모드에서는 내부·외부 모두 쓰기 거부

---

## 3. P0 — PowerPoint 대상 문서 동일성 보장

### 3.1 현재 문제

`PowerPointInspect` 이후 사용자가 활성 프레젠테이션을 바꾸면 `PowerPointEdit`가 새
`ActivePresentation`에서 같은 슬라이드 인덱스와 도형 ID를 찾아 수정한다. 슬라이드·도형 ID는 다른
프레젠테이션에서도 겹칠 수 있다.

### 3.2 입력·출력 계약 변경

검사 결과에 안정적인 문서 식별 정보를 추가한다.

```csharp
public sealed record PresentationIdentity(
    string Name,
    string? FullPath,
    string SessionToken);
```

`SessionToken`은 검사 시점의 PowerPoint 인스턴스와 프레젠테이션을 현재 CLI 세션에서 식별하는 불투명
값이다. 파일 경로가 있는 문서는 정규화된 `FullName`을 주 식별자로 사용한다. 저장되지 않은 문서는
COM 객체를 툴 밖으로 노출하지 말고 STA 내부 레지스트리에 토큰과 COM identity를 짧게 보관한다.

편집 입력은 다음 필드를 요구한다.

```json
{
  "action": "set_text",
  "presentation": {
    "full_path": "C:\\work\\deck.pptx",
    "session_token": "ppt-..."
  },
  "slide_id": 257,
  "shape_id": 4,
  "text": "변경된 제목"
}
```

우선순위:

1. 프레젠테이션 session token 또는 정규화된 전체 경로 일치
2. `slide_id` 일치
3. `shape_id` 일치
4. `shape_name`은 사용자 표시와 명시적 fallback에만 사용

`slide_index`는 표시·탐색 보조값으로 유지하되 쓰기 대상의 주 식별자로 사용하지 않는다.

### 3.3 쓰기 직전 검증

STA 스레드 안에서 다음을 한 번에 수행한다.

```text
PowerPoint 인스턴스 확인
→ 활성 프레젠테이션 identity 비교
→ 다르면 수정하지 않고 오류
→ slide_id로 슬라이드 탐색
→ shape_id로 도형 탐색
→ 텍스트 프레임과 읽기 전용 상태 확인
→ 수정
```

문서가 바뀐 경우 자동으로 새 활성 문서에 적용하지 않는다.

```text
PowerPointEdit: 대상 프레젠테이션이 변경되었습니다.
기대: C:\work\deck.pptx
현재: C:\work\other.pptx
PowerPointInspect를 다시 실행하세요.
```

### 3.4 테스트

COM 접근을 `IPowerPointAutomation` 같은 어댑터 뒤로 옮겨 fake로 검사한다.

- 검사 후 활성 프레젠테이션 변경 → 편집 거부
- 같은 경로지만 다른 `session_token` → 저장되지 않은 문서에서는 거부
- 슬라이드 순서 변경 후에도 `slide_id`로 올바른 슬라이드 수정
- 다른 슬라이드에 같은 `shape_id`가 있어도 수정하지 않음
- 대상 도형 삭제 후 실행 → 명확한 오류
- PowerPoint가 보호된 보기/읽기 전용 → 편집 거부

---

## 4. P1 — DOCX/PPTX/XLSX 원자적 생성

### 4.1 현재 문제

생성 툴이 최종 경로를 즉시 열어 작성한다. 중간에 예외가 발생하면 기존 문서가 이미 잘렸거나 부분
생성 파일이 남는다.

### 4.2 공통 원자적 쓰기 헬퍼

같은 디렉터리에 임시 파일을 만들어야 최종 이동이 동일 파일시스템에서 수행된다.

```csharp
internal static class AtomicDocumentWriter
{
    public static void Write(
        string finalPath,
        Action<string> create,
        Action<string> validate)
    {
        var directory = Path.GetDirectoryName(finalPath)
            ?? throw new InvalidOperationException("출력 디렉터리가 없습니다.");
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            create(tempPath);
            validate(tempPath);
            File.Move(tempPath, finalPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch
            {
                // 원래 생성/검증 오류를 가리지 않는다. 필요하면 observer에 경고 기록.
            }
        }
    }
}
```

주의 사항:

- 임시 파일 확장자가 `.tmp`여도 Open XML SDK는 파일 내용으로 처리할 수 있는지 확인한다. 문제가
  있으면 `.<guid>.xlsx`처럼 원래 확장자를 유지한다.
- 검증이 실패하면 기존 최종 파일은 보존한다.
- Windows에서 최종 파일이 PowerPoint/Excel에 열려 잠겨 있으면 교체를 실패시키고 기존 파일을 보존한다.
- `File.Copy` 후 삭제보다 동일 볼륨 `File.Move(..., overwrite:true)`를 우선한다.
- 체크포인트 observer는 최종 교체 직전에 기존 파일을 백업할 수 있어야 한다.

### 4.3 생성 후 검증

각 형식으로 다시 열고 `OpenXmlValidator`를 실행한다.

```csharp
private static void ValidateXlsx(string path)
{
    using var doc = SpreadsheetDocument.Open(path, false);
    var errors = new OpenXmlValidator().Validate(doc).Take(20).ToList();
    if (errors.Count > 0)
    {
        throw new InvalidDataException(
            "생성된 XLSX가 유효하지 않습니다: " +
            string.Join("; ", errors.Select(e => e.Description)));
    }
}
```

검증기는 실제 Office 렌더링을 완전히 보장하지 않으므로 실렌더 테스트도 별도로 유지한다.

### 4.4 테스트

- 기존 유효한 `report.xlsx` 준비
- 잘못된 차트 범위로 같은 경로에 생성 시도
- 툴은 오류 반환
- 기존 파일 해시와 내용은 그대로
- 임시 파일이 남지 않음
- 정상 생성은 최종 파일로 교체
- 대상 파일 잠금 시 기존 파일 보존
- DOCX/PPTX/XLSX 모두 같은 계약 적용

---

## 5. P1 — XLSX A1 범위 파서와 차트 정합성 검증

### 5.1 현재 문제

현재 파서는 `Split(':')`과 `int.Parse`만 사용한다. 절대 참조, 시트 접두사, 역방향 범위, 2차원 범위,
Excel 최대 행·열 범위, categories/values 길이 차이를 명시적으로 거부하지 않는다.

### 5.2 지원 계약을 좁게 정의

1차 구현에서 허용할 형식:

```text
A2
A2:A11
$A$2:$A$11
XFD1:XFD1048576
```

거부할 형식:

```text
A:A                    전체 열
1:10                   전체 행
A2:B10                 다중 열
Sheet2!A2:A10          다른 시트 참조
A10:A2                 역방향
A0:A10                 0행
XFE1:XFE2              Excel 최대 열 초과
A1:A1048577            Excel 최대 행 초과
```

### 5.3 값 객체와 파서

```csharp
internal readonly record struct VerticalRange(
    int ColumnIndex,
    int StartRow,
    int EndRow)
{
    public int Count => EndRow - StartRow + 1;
}
```

파서는 정규화된 수식 문자열도 함께 제공하는 편이 좋다.

```csharp
internal static bool TryParseVerticalRange(
    string input,
    out VerticalRange range,
    out string? error);
```

검증 항목:

- 입력 trim
- `$` 허용 후 제거
- 열 1~3자 영문
- 행은 1 이상 정수
- 열 범위 `A`~`XFD`(0~16383)
- 행 범위 1~1,048,576
- 시작과 끝 열 동일
- 시작 행 ≤ 끝 행
- 토큰 전체가 소비됐는지 확인

정규식만으로 끝내지 말고 파싱 후 Excel 상한을 검사한다.

### 5.4 차트 교차 검증

차트 생성 전에 다음을 검증한다.

- categories 범위와 모든 series values 범위의 길이가 동일
- 참조 행이 제공된 `rows` 범위를 벗어나지 않음
- 각 values 셀이 숫자로 파싱 가능하거나 명시적인 빈 값 정책을 따름
- `nameRef`는 단일 셀만 허용
- anchor는 단일 셀만 허용
- 원형 차트는 시리즈를 하나만 허용하거나 나머지를 무시하지 말고 오류로 알림
- 알 수 없는 chart type은 bar로 묵시적 fallback하지 말고 거부

빈 값과 비숫자 값을 무조건 `0`으로 바꾸면 실제 데이터와 다른 차트가 만들어진다. 정책을 명확히
선택한다.

권장 정책:

- 빈 셀: 캐시 포인트 생략 또는 gap
- 비숫자 셀: 생성 오류와 셀 주소 반환
- 사용자가 명시적으로 `coerceInvalidToZero=true`를 선택한 경우에만 0 변환

### 5.5 테스트 매트릭스

- 정상: `A2:A4`, `$A$2:$A$4`, `AA10:AA20`, `XFD1:XFD2`
- 거부: `A2:B4`, `A4:A2`, `A0:A2`, `XFE1:XFE2`, `A1:A1048577`
- 거부: `Sheet2!A2:A4`, `A:A`, 빈 문자열, 공백 포함 쓰레기
- categories 3개 / values 2개 → 오류
- `nameRef=B1:B2` → 오류
- `anchor=D2:D3` → 오류
- 비숫자 values 셀 → 셀 주소가 포함된 오류
- 잘못된 입력이 기존 XLSX를 손상시키지 않음(원자적 저장과 통합 테스트)

---

## 6. P1 — 취소 가능한 STA 큐와 Office 자원 수명

### 6.1 현재 문제

- `StaDispatcher.InvokeAsync`가 `CancellationToken`을 받지 않는다.
- 사용자가 요청을 취소해도 큐에 대기 중인 편집이 나중에 실행될 수 있다.
- `OfficeTools.CreateIfAvailable`이 만든 dispatcher를 `AppRuntime`이 소유·해제하지 않는다.
- late-binding으로 얻은 COM RCW를 명시적으로 해제하지 않아 장기 세션에서 누적될 수 있다.
- `CreateIfAvailable`이라는 이름과 달리 현재는 Office 설치 여부를 확인하지 않고 Windows이면 툴을 등록한다.

### 6.2 취소 가능한 작업 항목

```csharp
private sealed record WorkItem(Action Run, CancellationToken CancellationToken);

public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken ct = default)
{
    ct.ThrowIfCancellationRequested();
    var tcs = new TaskCompletionSource<T>(
        TaskCreationOptions.RunContinuationsAsynchronously);

    var item = new WorkItem(() =>
    {
        if (ct.IsCancellationRequested)
        {
            tcs.TrySetCanceled(ct);
            return;
        }

        try { tcs.TrySetResult(func()); }
        catch (Exception ex) { tcs.TrySetException(ex); }
    }, ct);

    // queue에 추가. Dispose/취소 경합은 TrySet 계열로 처리.
    return tcs.Task;
}
```

COM 호출이 이미 시작된 뒤에는 안전하게 강제 중단하기 어렵다. 계약을 다음처럼 정의한다.

- 큐 대기 중 취소: 작업 실행 금지
- COM 쓰기 시작 전 취소: 실행 금지
- COM 호출 시작 후 취소: 호출 종료까지 기다리되 추가 단계·저장은 중단
- 완료된 단일 COM 속성 변경을 취소로 되돌린다고 약속하지 않음

모든 Office 툴은 전달받은 `ct`를 dispatcher에 넘긴다.

```csharp
result = await _sta.InvokeAsync(() => SetText(inp), ct).ConfigureAwait(false);
```

### 6.3 런타임 소유권

도구 목록만 반환하지 말고 disposable 런타임을 반환한다.

```csharp
public sealed class OfficeToolRuntime : IDisposable
{
    private readonly StaDispatcher _dispatcher;
    public IReadOnlyList<ITool> Tools { get; }

    public void Dispose() => _dispatcher.Dispose();
}
```

`AppRuntime`이 `McpManager`와 함께 `OfficeToolRuntime?`을 소유하고 종료 시 해제하도록 한다. 더 나은
형태는 `AppRuntime` 자체가 `IAsyncDisposable`을 구현해 모든 하위 수명을 한곳에서 관리하는 것이다.

```csharp
await using var runtime = await AppBootstrap.BuildAsync(...);
```

### 6.4 COM RCW 해제

COM 객체는 반드시 STA 스레드 안에서 leaf-first 순서로 해제한다.

```csharp
private static void ReleaseCom(object? value)
{
    if (value is not null && Marshal.IsComObject(value))
    {
        Marshal.FinalReleaseComObject(value);
    }
}
```

권장 패턴:

```csharp
object? app = null;
object? presentation = null;
object? slide = null;
object? shape = null;
try
{
    // COM 작업
}
finally
{
    ReleaseCom(shape);
    ReleaseCom(slide);
    ReleaseCom(presentation);
    ReleaseCom(app);
}
```

주의:

- 동일 RCW를 여러 변수가 공유할 때 중복 `FinalReleaseComObject`하지 않는다.
- `foreach`로 COM collection을 순회하면 숨은 enumerator RCW가 생길 수 있으므로 1-based index 접근을
  유지한다.
- `app.ActiveWindow.View.Slide`처럼 점 연산을 길게 연결하면 중간 RCW를 해제할 수 없다. 단계별 변수로
  분리한다.
- 실행 중인 사용자의 Office 애플리케이션에 연결한 경우 `Quit()`를 호출하지 않는다.

### 6.5 Office 가용성

두 정책 중 하나를 선택해 이름·동작을 일치시킨다.

1. 지연 연결 유지: Windows에서는 항상 툴을 등록하고 실행 시 “Office 미설치/미실행”을 구분해 안내.
2. 실제 가용성 검사: ProgID 등록 여부를 확인해 설치되지 않은 앱의 툴은 등록하지 않음.

현재 동작은 1번에 가까우므로 `CreateForWindows` 같은 이름이 더 정확하다.

### 6.6 테스트

- 큐에 대기 중인 쓰기 작업 취소 → delegate가 호출되지 않음
- enqueue 전 취소 → 즉시 `TaskCanceledException`
- Dispose와 enqueue 경합 → 작업이 실행되거나 명확히 취소/폐기되며 hang 없음
- dispatcher 종료 후 스레드가 살아 있지 않음
- `AppRuntime.DisposeAsync`가 Office dispatcher까지 해제
- Windows 통합 테스트에서 반복 inspect/edit 후 PowerPoint·Excel 프로세스 핸들·RCW가 지속 증가하지 않음

---

## 7. 실제 Office 상호운용 테스트

`OpenXmlValidator` 0건은 스키마 유효성을 의미할 뿐 PowerPoint/Excel의 정상 렌더링을 보장하지 않는다.
기존에 다음 문제가 validator를 통과한 뒤 실렌더에서 발견됐다.

- PPTX 도형 위치·크기 누락으로 공백 슬라이드
- layout→master 역관계 누락으로 PowerPoint 복구 경고
- XLSX 차트 series 채우기 누락으로 막대·조각 미표시

### 7.1 CI 계층

| 계층 | 환경 | 검증 |
|---|---|---|
| 빠른 단위 테스트 | Linux/Windows | 파서, 경로, 권한, DTO, STA 큐 |
| Open XML 구조 테스트 | 전 플랫폼 | 생성→재오픈→`OpenXmlValidator` |
| LibreOffice 실렌더 | Linux CI | PPTX→PDF/PNG, XLSX→PDF/PNG 변환 성공·비어 있지 않음 |
| Microsoft Office smoke | Windows 전용 | PowerPoint/Excel 열기, 복구 경고 없음, 핵심 객체 존재 |
| COM 통합 테스트 | Windows 수동/격리 CI | 검사→편집→화면/문서 상태 확인 |

### 7.2 실렌더 최소 수용 기준

- 생성 파일을 LibreOffice headless로 열고 PDF 또는 PNG로 변환 가능
- 출력 페이지 수가 입력 슬라이드·시트 기대치와 일치
- 출력 파일 크기가 0이 아님
- 텍스트 추출 결과에 제목·표 헤더·핵심 데이터 포함
- 차트가 있는 문서는 렌더 이미지에서 비백색 픽셀 영역 또는 객체 존재 확인
- Windows PowerPoint/Excel에서 “복구하시겠습니까?” 경고 없이 열림

픽셀 비교는 폰트·플랫폼 차이로 불안정하므로 전체 스냅샷보다 핵심 영역과 구조 검사를 병행한다.

---

## 8. 완료 조건

패치는 다음 조건을 모두 만족해야 완료로 본다.

- [ ] 모든 로컬 쓰기 툴이 공통 쓰기 대상 계약을 구현한다.
- [ ] 워크스페이스 경계 검사가 영속 allow보다 먼저 실행된다.
- [ ] 외부 경로 쓰기는 대화형 확인 없이는 실행되지 않는다.
- [ ] PowerPoint 편집 입력이 프레젠테이션 identity와 `slide_id`를 포함한다.
- [ ] 활성 문서가 바뀌면 편집하지 않고 재검사를 요구한다.
- [ ] DOCX/PPTX/XLSX 생성은 임시 파일→검증→원자 교체 순서다.
- [ ] 생성 실패 시 기존 최종 파일과 해시가 보존된다.
- [ ] XLSX 차트 범위가 전용 파서로 검증된다.
- [ ] categories/series 길이와 숫자 데이터 정합성을 검사한다.
- [ ] 취소된 STA 대기 작업은 실행되지 않는다.
- [ ] `AppRuntime` 종료 시 Office dispatcher가 해제된다.
- [ ] 전체 단위/Open XML 테스트가 통과한다.
- [ ] LibreOffice 실렌더와 Windows Office smoke 검증이 통과한다.

현재 기준 회귀 베이스라인:

```text
MoaiCode.Core.Tests:     387
MoaiCode.OpenXml.Tests:   19
MoaiCode.Office.Tests:    12
합계:                    418
```

패치 이후에는 위 418개가 그대로 통과하고 본 문서의 신규 경계·실패·취소 테스트가 추가로 통과해야 한다.

---

## 9. 패치 분할 권장안

리뷰와 회귀 추적을 위해 한 번에 모두 섞지 않는다.

```text
1. security: workspace write-target contract and gate ordering
2. openxml: atomic document creation and validation
3. xlsx: strict A1 range parser and chart input validation
4. office: stable presentation/slide identity for PowerPointEdit
5. office: cancellable STA queue and runtime disposal
6. tests: LibreOffice and Windows Office interoperability smoke coverage
```

각 커밋은 해당 문제의 실패 테스트를 먼저 포함하고, 관련 없는 리팩터링이나 포맷 변경을 섞지 않는다.
