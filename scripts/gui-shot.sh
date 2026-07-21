#!/usr/bin/env bash
# MoAI Docs(Avalonia GUI)를 헤드리스(Xvfb)로 렌더해 PNG 로 캡처한다.
# 데스크톱 환경 없는 리눅스에서 GUI 를 눈으로 미리보기/반복하기 위한 개발 도구.
#   사용: scripts/gui-shot.sh [out.png] [width] [height] [wait_sec]
set -uo pipefail
cd "$(dirname "$0")/.."
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"; export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

OUT="${1:-/tmp/moai-gui.png}"
W="${2:-1120}"; H="${3:-760}"; WAIT="${4:-8}"
DLL="src/MoaiCode.Gui/bin/Debug/net10.0/MoaiCode.Gui.dll"

echo "빌드 중…"
dotnet build src/MoaiCode.Gui/MoaiCode.Gui.csproj -c Debug -v q \
  --disable-build-servers -nodereuse:false >/dev/null 2>&1 || { echo "❌ 빌드 실패"; exit 1; }

pkill -9 Xvfb 2>/dev/null; sleep 1
Xvfb :99 -screen 0 "${W}x${H}x24" >/dev/null 2>&1 & XPID=$!
sleep 2
DISPLAY=:99 LANG=ko_KR.UTF-8 dotnet "$DLL" >/tmp/moai-gui-run.log 2>&1 & APP=$!
sleep "$WAIT"
if DISPLAY=:99 import -window root "$OUT" 2>/dev/null; then
  echo "✅ 캡처: $OUT ($(du -h "$OUT" | cut -f1))"
else
  echo "❌ 캡처 실패"; tail -5 /tmp/moai-gui-run.log
fi
kill -9 "$APP" "$XPID" 2>/dev/null
exit 0
