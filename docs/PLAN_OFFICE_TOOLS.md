# 작업지시서: PowerPoint/Excel 실시간 편집 (Office COM + Open XML)

원본 설계 `docu_work_interaction.md` 를 실착수 가능한 형태로 구체화한다. 원문의 **COM(열린 문서
실시간) + Open XML(닫힌 문서/신규 생성) 하이브리드** 판단, 툴 목록, STA/DTO 실행 모델은 타당하므로
유지한다. 이 문서는 원문이 비워 둔 **빌드 통합·격리·리스크 검증**을 채운다.

## 0. 이 문서가 원문에 더하는 것

원문은 COM vs Open XML 선택과 툴 설계는 잘 잡았으나, **이 코드베이스의 빌드 현실**을 다루지 않는다.
현재 구조에서 그대로 진행하면 깨지는 지점:

| 현재 사실 (확인됨) | 그대로 진행 시 문제 |
|---|---|
| 모든 프로젝트가 `Directory.Build.props` 에서 `net10.0` 강제 | Office COM 은 `net10.0-windows` 필요 → override 필수 |
| `MoaiCode.Cli` 가 **단일 csproj**에서 7개 RID(win/linux/osx) 크로스 게시 | `net10.0-windows` 프로젝트를 정적 참조하면 **Linux/macOS 게시가 컴파일 에러** |
| 리플렉션 기반 어셈블리 플러그인 로더 **없음**(스킬은 파일 기반) | "런타임에 Office DLL 로드"는 **새 메커니즘**을 만들어야 함 |
| `PublishSingleFile=true` + 트리밍 | COM Interop(`EmbedInteropTypes`)이 single-file 과 어떻게 상호작용하는지 **미검증** |
| `InvariantGlobalization=true` | Excel 수식/숫자 서식의 로케일 처리에 영향 가능 |

## 1. 아키텍처 결정: Cli 멀티타깃

**결정: `MoaiCode.Cli` 를 `net10.0;net10.0-windows` 멀티타깃으로 만든다.**

- 이유: 원문 의도는 "하나의 제품, Windows 빌드에만 Office 툴 추가"(원문 L364–370). 리플렉션
  플러그인 로딩은 single-file 배포에서 외부 DLL 위치·서명·로딩이 복잡해진다. 멀티타깃은
  single-file 을 유지하면서 COM 참조를 Windows 빌드에만 넣는 가장 idiomatic 한 방법이다.
- win-* RID 게시 → `net10.0-windows` 타깃 선택 → Office 툴 포함.
- linux/osx RID 게시 → `net10.0` 타깃 → Office 툴 없음(Open XML 툴만).

```
MoaiCode.Cli  (net10.0 ; net10.0-windows)
   └─ (net10.0-windows 일 때만) ─▶ MoaiCode.Tools.Office  (net10.0-windows)
                                        ├─ PowerPoint*  (COM)
                                        ├─ Excel*       (COM)
                                        └─ STA dispatcher
MoaiCode.Tools.OpenXml  (net10.0)   ◀─ 모든 RID (닫힌 문서 생성/검증)
```

**대안(비채택): 리플렉션 플러그인 로더.** Office 툴을 별도 DLL 로 두고 Windows 에서만
`Assembly.LoadFrom`. single-file 자기추출 디렉토리에 Office DLL 을 심어야 하고, 로딩 실패/버전
불일치 처리가 늘어난다. 멀티타깃이 더 단순하면 채택하지 않는다.

### 1.1 구체적 변경

`MoaiCode.Cli.csproj`:
```xml
<PropertyGroup>
  <!-- Directory.Build.props 의 net10.0 를 덮어씀 -->
  <TargetFrameworks>net10.0;net10.0-windows</TargetFrameworks>
</PropertyGroup>
<ItemGroup Condition="'$(TargetFramework)' == 'net10.0-windows'">
  <ProjectReference Include="..\MoaiCode.Tools.Office\MoaiCode.Tools.Office.csproj" />
</ItemGroup>
```
> `Directory.Build.props` 는 `<TargetFramework>`(단수)를 설정한다. 멀티타깃 프로젝트는
> `<TargetFrameworks>`(복수)로 덮어써야 하며, props 가 단수를 조건 없이 넣으면 충돌한다.
> → props 의 해당 줄을 `Condition="'$(TargetFrameworks)' == ''"` 로 가드해야 한다(선행 작업).

`MoaiCode.Tools.Office.csproj` (신규):
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWindowsForms>false</UseWindowsForms>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\MoaiCode.Core\MoaiCode.Core.csproj" />
    <!-- Office Interop: NuGet PIA. late-binding(dynamic) 시 불필요할 수도 — 스파이크에서 확정 -->
    <PackageReference Include="Microsoft.Office.Interop.PowerPoint" />
    <PackageReference Include="Microsoft.Office.Interop.Excel" />
  </ItemGroup>
</Project>
```

### 1.2 툴 등록 (조건부)

`ITool` 계약은 그대로 구현한다: `Name / Description / InputSchema / IsReadOnly /
IsConcurrencySafe / ExecuteAsync`. 조회 툴은 `IsReadOnly=true`(권한 게이트 자동 통과),
편집/저장/삭제 툴은 `IsReadOnly=false`(쓰기 확인 게이트 통과 — 이미 구현됨).

`AppBootstrap.BuildAsync` 의 `toolList` 조립부(현재 `new List<ITool>(ToolRegistry.BuiltIn)`)에
조건부 등록을 추가:
```csharp
#if WINDOWS
if (OperatingSystem.IsWindows())
{
    toolList.AddRange(MoaiCode.Tools.Office.OfficeTools.CreateIfAvailable());
}
#endif
```
`CreateIfAvailable()` 는 Office 미설치/COM 차단 시 **빈 목록**을 반환한다(런타임 안전).

## 1.3 개발 환경 제약 (실측 확인됨)

이 저장소는 Linux 에서 개발/게시된다. Office 기능의 어디까지가 Linux 에서 가능한지 실측했다:

| 항목 | Linux 에서 | 근거 |
|---|---|---|
| `net10.0-windows` 빈 라이브러리 빌드 | ✅ 가능 | 실측: 빈 라이브러리 0 에러 컴파일 |
| Office Interop PIA NuGet restore | ✅ 가능 | 실측: `Microsoft.Office.Interop.Excel` restore 성공 |
| Cli 멀티타깃 + 7개 RID 게시 격리 검증 | ✅ 가능 | 골격만으로 게시가 깨지는지 확인 가능 |
| late-binding(`dynamic`) COM 코드 **컴파일** | ✅ 가능(예상) | COM 호출은 런타임에만 Windows 필요 |
| **COM 자동화 실제 동작**(PowerPoint 연결·편집) | ❌ **Windows 필수** | Phase 0 스파이크는 Windows 에서만 |

**결론**: **빌드 통합(§1.1, §1.2, §4)과 골격은 Linux 에서 작성·검증 가능**하다. 오직 **런타임 COM
동작 검증(Phase 0 S1–S3, Phase 1–4 의 "Windows 실동작")만 Windows 개발기가 필요**하다. 따라서
착수를 두 트랙으로 나눈다:
- **트랙 L (Linux)**: 프로젝트 구조, 멀티타깃, 격리 게시, 툴 골격/DTO/STA 스캐폴딩, Open XML 툴.
- **트랙 W (Windows)**: Phase 0 스파이크 + COM 실동작. Windows + Office 설치 개발기 필요.

## 2. Phase 0 — 리스크 스파이크 (트랙 W · Windows 필수, 1–2일)

전체 구현 전에 **가장 불확실한 3가지**를 실제 Windows + 실제 PowerPoint 로 검증한다. 실패하면
아키텍처를 바꿔야 하므로 반드시 선행.

- [ ] **S1. single-file + COM**: `PublishSingleFile=true` 로 게시한 win-x64 self-contained
      바이너리가 실행 중 PowerPoint 에 COM 연결되는가? (`EmbedInteropTypes` vs late-binding
      `Type.GetTypeFromProgID("PowerPoint.Application")` + `dynamic` 비교. late-binding 이면
      PIA/트리밍 문제를 회피할 수 있음.)
- [ ] **S2. STA 디스패처**: 전용 STA 스레드 작업 큐에서 COM 호출을 직렬화하고, 툴 스레드에는
      COM 객체가 아닌 DTO 만 넘기는 최소 구현이 도는가? (원문 L260–276)
- [ ] **S3. InvariantGlobalization 영향**: `InvariantGlobalization=true` 상태에서 Excel COM 의
      수식 문자열/숫자 서식이 깨지지 않는가? 깨지면 Office 툴 경로에서만 culture 를 복원하는 방법 확인.

산출물: `spike/OfficeComSpike/` 최소 콘솔 앱 + 결과 메모. **S1 이 실패하면** 멀티타깃 대신
"별도 non-single-file Windows 번들"로 방향 전환.

## 3. 구현 단계 (원문 4단계를 이 코드베이스에 매핑)

원문의 단계별 계획(L326–362)을 유지하되, 각 단계에 이 코드베이스의 통합 지점을 붙인다.

### Phase 1 — 프로젝트 골격 + 조회(읽기전용)
- `MoaiCode.Tools.Office` (net10.0-windows) 생성, Cli 멀티타깃 전환, `Directory.Build.props` 가드.
- `StaDispatcher`, `PowerPointSession`/`ExcelSession`(연결·활성 문서 조회), DTO 정의.
- `PowerPointInspectTool` / `ExcelInspectTool` (`IsReadOnly=true`).
- **검증(goal)**: linux/osx/win 7개 RID 게시가 **전부 성공**(격리 확인). Windows 에서
  `PowerPointInspect` 가 활성 프레젠테이션의 슬라이드/도형을 JSON 으로 반환.

### Phase 2 — 실시간 편집(쓰기)
- `PowerPointEditTool` / `ExcelEditTool` / 이미지 / 슬라이드 툴. 전부 `IsReadOnly=false`.
- **쓰기 직전 재확인**(원문 L278–290): 활성 문서 동일성·대상 슬라이드/도형/범위 유효성.
  조회 결과를 그대로 믿고 쓰지 않는다.
- 삭제(슬라이드·도형·워크시트)·덮어쓰기·다른 경로 내보내기 → **권한 확인 대상**. 이번 세션에
  만든 `ModeAwarePermissionGate` 가 이미 처리하지만, Office 파괴적 액션을 `BashSecurity` 처럼
  "확인 티어"로 분류할지 검토(예: `PowerPointSlide{action:delete}` 는 확인).

### Phase 3 — Open XML(닫힌 문서/생성/검증)
- `MoaiCode.Tools.OpenXml` (net10.0, 전 플랫폼). `DocumentFormat.OpenXml` NuGet.
- 신규 PPTX/DOCX/XLSX 생성, 닫힌 문서 일괄 수정, 구조 검증.
- **crossgen/트리밍**: OpenXml 이 트리밍에 안전한지 확인(리플렉션 사용 시 `TrimmerRootAssembly`).

### Phase 4 — 고급(표/차트/피벗/교차앱/Undo/이미지 생성)
- Excel 차트 → PowerPoint 삽입 교차앱. 이미지 생성 API 는 `HttpClient`(기존 프록시 설정 재사용).

## 4. publish-all.sh 변경

```bash
for RID in "${RIDS[@]}"; do
  TFM="net10.0"; [[ "$RID" == win-* ]] && TFM="net10.0-windows"
  dotnet publish src/MoaiCode.Cli/MoaiCode.Cli.csproj -r "$RID" -f "$TFM" "${COMMON[@]}" -o "$OUT"
done
```
> win-* 는 `-f net10.0-windows`, 나머지는 `-f net10.0`. 멀티타깃이면 `-f` 없이 게시 시 에러가
>나므로 명시 필수.

## 5. 배포/운영 전제 (원문 L364–370 확정)

- Office 실시간 툴: **Windows + Office 데스크톱 설치 + COM 자동화 미차단** 전제.
- 외부 고객사 배포이므로 **그룹정책(GPO)로 Office COM/매크로가 차단**되어 있으면 동작 불가 →
  `CreateIfAvailable()` 가 조용히 비활성(에러 대신 "Office 연결 불가" 안내).
- Linux/macOS 빌드: Office COM 툴 미등록, Open XML 툴만.
- **AppLocker/WDAC 해시 화이트리스트**(이번 세션에서 다룸): Windows 빌드가 별도 TFM 이면
  **바이너리 해시가 달라지므로** SHA-256 목록에 win 빌드가 정확히 반영되는지 확인.

## 6. 리스크 요약

| 리스크 | 완화 |
|---|---|
| single-file + COM Interop 미검증 | **Phase 0 S1** 스파이크로 선검증. 실패 시 non-single-file Windows 번들 |
| 크로스 RID 게시 깨짐 | 멀티타깃 + 조건부 참조. Phase 1 goal 에 "7개 RID 전부 성공" 명시 |
| COM 스레딩 사고 | STA 단일 큐 + DTO 경계(원문 L260–276). COM 객체를 툴 스레드로 넘기지 않음 |
| 고객사 GPO 차단 | `CreateIfAvailable()` graceful 비활성 |
| Excel 로케일(InvariantGlobalization) | Phase 0 S3 + invariant 수식 우선(원문 L233) |
| 저장되지 않은 사용자 편집 유실 | 쓰기 직전 재확인 + 명시적 저장(자동저장 안 함, 원문 L195) |

## 7. 착수 순서 (권장)

1. **Phase 0 스파이크** — S1/S2/S3. 여기서 방향 확정.
2. `Directory.Build.props` 가드 + Cli 멀티타깃 + 빈 `MoaiCode.Tools.Office` 골격 →
   **7개 RID 게시가 전부 통과하는지 먼저 확인**(빈 프로젝트로 격리 검증).
3. Phase 1(조회) → 2(편집) → 3(OpenXml) → 4(고급).

각 단계는 "7개 RID 게시 성공 + Windows 실동작"을 goal 로 두고 verify.
