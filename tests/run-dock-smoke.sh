#!/usr/bin/env bash
# dock 스모크 하네스 한 명령 실행: dock-smoke(레이아웃) + dock-resize-smoke(리사이즈).
# 완료 판정: 종료 코드 0 = 전부 통과, 1 = 하나라도 실패. publish-linux.sh 게이트에서도 호출됨.
set -uo pipefail
cd "$(dirname "$0")/.."

# dotnet 은 PATH 에 없고 사용자 홈에 설치돼 있다(publish-linux.sh 와 동일 절차).
if ! command -v dotnet >/dev/null 2>&1; then
  if [ -x "$HOME/.dotnet/dotnet" ]; then
    export DOTNET_ROOT="$HOME/.dotnet"
    export PATH="$DOTNET_ROOT:$PATH"
  else
    echo "❌ dotnet 을 찾을 수 없습니다." >&2
    exit 1
  fi
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1

FAIL=0

echo "══ [1/3] dock-smoke 빌드 ══"
dotnet build tests/dock-smoke/dock-smoke.csproj --nologo -v q || { echo "❌ dock-smoke 빌드 실패"; exit 1; }

echo "══ [2/3] dock-smoke: 레이아웃(구분선/입력행/상태줄) ══"
dotnet run --project tests/dock-smoke/dock-smoke.csproj --no-build || FAIL=1

echo ""
echo "══ [3/3] dock-resize-smoke: 리사이즈 시퀀스(모델 A/B) ══"
if command -v python3 >/dev/null 2>&1; then
  dotnet build tests/dock-resize-smoke/dock-resize-smoke.csproj --nologo -v q || { echo "❌ dock-resize-smoke 빌드 실패"; FAIL=1; }
  if [ "$FAIL" -eq 0 ]; then
    python3 tests/dock-resize-smoke/run.py || FAIL=1
  fi
else
  echo "⚠ python3 없음 — dock-resize-smoke 건너뜀(실패로 처리하지 않음)"
fi

echo ""
if [ "$FAIL" -eq 0 ]; then
  echo "✅ DOCK SMOKE ALL PASS"
else
  echo "❌ DOCK SMOKE FAILED"
fi
exit "$FAIL"
