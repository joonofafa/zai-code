# MoAI Code — C# 변환 (cs_conv)

MoAI Code — OpenClaude(TypeScript 코딩 에이전트 CLI)를 기반으로 한 C# 포트. 전체 변환 계획은 상위 디렉토리의
[`CSHARP_PORT_PLAN.md`](../CSHARP_PORT_PLAN.md) 참고.

## 설계 목표

- **Windows 11에서 런타임 설치 없이 단일 exe로 구동** — self-contained 단일 파일 게시.
- .NET 10 (LTS) 타깃. 빌드 머신은 Linux/macOS/Windows 무관(크로스 게시 지원).
- UI는 Spectre.Console 기반 MVU(React/Ink 대체).

## 솔루션 구조

```
MoaiCode.Core         도메인 모델, 에이전트 루프, Tool/Model 추상화 (외부 의존성 없음)
MoaiCode.Providers    프로바이더/모델 — OpenAI shim, Anthropic, Gemini, Ollama (Phase 1)
MoaiCode.Tools        파일/검색/웹/플랜/태스크 툴 (Phase 3)
MoaiCode.Tools.Bash   셸 실행 + 단순화 보안/권한 (Phase 4)
MoaiCode.Mcp          MCP 클라이언트, 스킬/플러그인 (Phase 6)
MoaiCode.Config       3-tier 설정, 권한, 인증 (Phase 5)
MoaiCode.Persistence  세션 저장/복원, 히스토리, 비용 (Phase 5)
MoaiCode.Tui          Spectre.Console MVU 터미널 UI (Phase 7)
MoaiCode.Cli          엔트리포인트, 커맨드 디스패치 (Phase 8)
MoaiCode.Sdk          공개 SDK 타입
```

현재 상태: **Phase 0 walking skeleton** — 오프라인 `EchoChatModel`로 REPL이 동작.

## 사전 요구사항 (개발/빌드 머신만)

- .NET 10 SDK ([dotnet.microsoft.com](https://dotnet.microsoft.com/download))
- **최종 사용자(Windows 11)는 아무것도 설치할 필요 없음** — 게시된 exe에 런타임 포함.

## 빌드 & 실행

```bash
cd cs_conv
dotnet build                       # 전체 솔루션 빌드
dotnet test                        # 단위 테스트
dotnet run --project src/MoaiCode.Cli   # REPL 실행
dotnet run --project src/MoaiCode.Cli -- --version
```

## Windows용 단일 exe 게시 (런타임 불필요)

Linux/macOS 또는 Windows 어디서든:

```bash
./publish-win.sh          # → dist/win-x64/moai.exe
# 또는 Windows PowerShell:  ./publish-win.ps1
```

`dist/win-x64/moai.exe` 하나만 Windows 11로 복사하면 실행됩니다.
ARM64 Windows는 `./publish-win.sh win-arm64`.

## 다음 단계

`CSHARP_PORT_PLAN.md`의 Phase 1(프로바이더/SSE) → Phase 2(에이전트 루프) →
Phase 3(파일 툴) 순으로 진행.
