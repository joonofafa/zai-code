# 핸드오프: GUI 스킬 배선(Phase 4) ↔ CLI 스킬 인프라 담당

수신: CLI/스킬 인프라 담당 에이전트
작성: GUI(MoAI Desktop) 담당
상태: GUI 배선 완료·빌드 통과. 아래 4개 조정/후속만 확인 요망.

## 배경
Phase 4로 데스크톱 GUI에 스킬 지원을 얹는 작업을 했다. 착수 시점에 Phase 3 스킬
인프라가 **미커밋 병렬 작업** 상태였다:
- `src/MoaiCode.Cli/TeamSkills.cs` = untracked(신규)
- `OpenMoaiClient.cs`, `AppBootstrap.cs`, `LoginFlow.cs`, `Mcp/Skills/SkillTool.cs`,
  TUI 파일들 = 수정됨(미커밋)

그래서 **dirty한 병렬 작업 파일은 일절 수정하지 않는** 원칙으로, GUI 쪽에서만 배선했다.

## GUI가 한 것(이번 커밋)
1. **`TeamSkills.cs` 를 CLI → Config 로 물리 이동**(`src/MoaiCode.Config/TeamSkills.cs`).
   - **네임스페이스는 `MoaiCode.Cli` 그대로 유지** → CLI/TUI 코드 **무수정**으로 resolve
     (CLI가 Config 참조, TUI는 TeamSkills 타입을 직접 안 씀=콜백 주입). CLI 빌드 통과 확인.
   - TeamSkills 의 의존(`OpenMoaiClient`, `TeamSkillItem`)이 원래 Config 에 있어 같은
     어셈블리로 모여 오히려 정합적.
2. **GUI → `MoaiCode.Mcp` 참조 추가**(SkillLoader/SkillTool/PluginLoader 사용).
3. **`GuiBootstrap.CuratedTools`** 에 스킬 수집 추가(`CollectSkillTool`): 사용자(`SkillLoader.
   Discover`) + 플러그인(`PluginLoader.Discover`) + 팀 공유(`LoadFromDir(TeamSkills.Dir)`)
   → `SkillTool`. 실패는 non-fatal.
4. **설정 화면 스킬 마스터 스위치**(`GuiSettings.SkillsEnabled`, 기본 on). off 면 SkillTool
   미등록. 토글 시 엔진 재구성으로 즉시 반영.

## 담당 에이전트가 확인/조정할 것 (4)
1. **TeamSkills 위치·네임스페이스**: 파일을 Config 로 옮겼으나 ns 는 `MoaiCode.Cli` 유지(불일치).
   Phase 3 커밋 시 이 이동을 반영하고, 원하면 ns 를 `MoaiCode.Config` 로 정리(그 경우 CLI/TUI
   using 조정 필요 — 지금은 무수정으로 동작 중). **CLI 의 원래 `TeamSkills.cs` 는 삭제됨**(untracked
   였음).
2. **BundledSkills(기본 번들 스킬) — GUI 에서 아직 제외**: `BundledSkills` 는 CLI 어셈블리의
   임베드 리소스(`Resources/bundled-skills.zip`)에 묶여 있어 GUI 로 옮기지 못했다(Config.csproj
   가 dirty 라 건드리지 않음). **공유 어셈블리(Config 또는 Mcp)로 `BundledSkills.cs` + zip
   리소스를 옮기면**, GUI `CollectSkillTool` 에 `BundledSkills.EnsureExtracted()` +
   `LoadFromDir(BundledSkills.Dir)` 3줄만 추가하면 GUI 도 번들 스킬을 쓴다.
3. **GUI 자체 팀 스킬 동기화 — 미배선**: "오버헤드 최소화" 방침으로 GUI 는 팀 스킬을 **재동기화하지
   않고** 이미 받아둔 `~/.moai/team-skills` 를 로드만 한다(동기화는 CLI/로그인 플로우 담당). GUI
   단독 사용자를 위해 로그인 시 백그라운드 `TeamSkills.SyncAsync` → 엔진 재구성 배선이 필요하면
   추가. (TryBuild 가 sync 라 UI 블로킹 없이 하려면 OnLoggedIn 백그라운드 권장.)
4. **dirty 파일 미수정 확인**: AppBootstrap/OpenMoaiClient/SkillTool/LoginFlow/TUI 등 진행 중
   파일은 건드리지 않았다. 커밋 충돌 없음.

## 검증
- GUI 빌드 / CLI 빌드 통과.
- `~/.moai/team-skills` 에 스킬이 있으면(웹 활성화 → CLI/로그인 동기화) GUI 에이전트의
  SkillTool 로 잡히고, 설정에서 스킬을 끄면 SkillTool 이 빠진다.
- 최종 팀 스킬 자동 사용 확인은 Windows 실기(로그인+웹 활성화)에서.
