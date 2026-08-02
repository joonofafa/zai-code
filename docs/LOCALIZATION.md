# MoAI Code 로컬라이제이션 가이드

## 목표

CLI/TUI/GUI가 동일한 언어 설정과 문자열 카탈로그를 사용하도록 만드는 것이 목적입니다. 첫 지원 언어는 한국어(`ko`)와 영어(`en`)입니다.

`Directory.Build.props`가 `InvariantGlobalization=true`를 사용하므로 `CurrentUICulture`나 위성 어셈블리 전환에 의존하지 않습니다. `MoaiCode.Localization`이 명시적 언어 코드와 임베드 JSON 카탈로그를 사용합니다.

## 구성

```text
SettingsLoader
  language / locale / uiLanguage
  MOAI_LANGUAGE
        │
        ▼
MoaiCode.Localization.L10n
  NormalizeLanguage
  SetLanguage
  Get / GetForLanguage
        │
        ├── CLI 도움말과 출력
        ├── TUI 슬래시 명령
        └── GUI ViewModel 바인딩
```

- 기본 언어: `ko`
- 폴백 순서: 선택 언어 → `en` → 리소스 키 자체
- 지역 코드: `ko-KR`, `en_US`처럼 입력해도 `ko`, `en`으로 정규화
- 런타임 선택: `moai language en`, `/language en`, GUI 설정의 표시 언어
- 영속화: `~/.moai/settings.json`의 `language`
- 일시적 오버라이드: `MOAI_LANGUAGE=en`

## 문자열 키 규칙

키는 화면 계층과 의미를 함께 표현합니다.

```text
cli.<command>.<purpose>
slash.<command>.<purpose>
gui.<view>.<purpose>
common.<purpose>
```

문장 자체를 키로 사용하지 않습니다. 같은 한국어 문장이라도 화면에서 의미가 다르면 별도 키를 사용합니다. 자리표시는 `{0}`, `{1}` 형식을 사용하며 `L10n.Get("key", value)`로 전달합니다.

## 새 언어 추가

1. `src/MoaiCode.Localization/Resources/<code>.json`을 추가합니다.
2. `L10n.LanguageList`에 언어 코드와 고유 표시명을 추가합니다.
3. 기존 `en.json` 키를 기준으로 모든 항목을 번역합니다.
4. `LocalizationTests`에 코드 정규화와 대표 문자열 검증을 추가합니다.
5. CLI 도움말, `/language list`, GUI 설정 선택기에서 노출되는지 확인합니다.

리소스 파일은 `EmbeddedResource`이므로 self-contained 단일 파일 게시에도 포함됩니다.

## 코드에서 사용하는 방법

```csharp
using MoaiCode.Localization;

Console.WriteLine(L10n.Get("cli.auth.saved", credentialName));
```

GUI XAML은 현재 ViewModel의 로컬라이즈된 속성에 바인딩합니다. 언어가 런타임에 바뀌는 화면은 관련 속성에 `PropertyChanged`를 발생시켜야 합니다.

## 번역 대상과 비대상

우선 번역 대상:

- CLI 도움말, 로그인·프록시 메시지
- TUI 상태·오류·권한·슬래시 명령 설명
- GUI XAML 라벨, 버튼, 상태·오류 메시지
- 사람이 읽는 문서 툴 결과

기본적으로 번역하지 않는 항목:

- 명령어 이름(`/language`, `run`)과 옵션 이름
- 툴 이름, JSON 필드, MCP 프로토콜 값
- 로그 검색에 사용하는 안정적인 오류 코드
- LLM 시스템 프롬프트와 툴 스키마 설명

LLM에 전달되는 텍스트는 UI 문자열과 성격이 다릅니다. 모델 동작과 토큰 비용에 영향을 줄 수 있으므로 UI 번역과 분리해 별도 정책으로 다룹니다.

## 현재 이관 범위

기반 작업에서 다음 경로를 이관했습니다.

- CLI의 명령·인자·옵션 도움말과 일부 공통 출력
- `language` CLI 명령과 `/language` 슬래시 명령
- 주요 TUI 슬래시 명령 설명
- GUI의 현재 인라인 설정 패널과 레거시 설정 창

로그인, 조직 검색, 도구 결과, 메인 채팅 화면에는 아직 하드코딩 문자열이 남아 있습니다. 이후에는 기능 변경과 섞지 않고 화면 또는 파일 단위로 카탈로그를 확장합니다.

System.CommandLine이 자체 생성하는 `Usage`, `Options`, 기본 도움말 문구는 현재 프레임워크 기본 언어를 사용합니다. 애플리케이션이 제공하는 명령·인자·옵션 설명은 카탈로그로 전환되었으며, 프레임워크 기본 문구의 완전한 번역은 사용자 정의 도움말 빌더 도입 단계에서 처리합니다.
