#!/usr/bin/env bash
# MoAI Desktop(GUI) 전용 게시 — win-x64 self-contained 단일 exe → ~/shareHub/Zone/.
#
# ⚠️ 핵심: IncludeNativeLibrariesForSelfExtract=true 를 반드시 포함한다.
#    누락 시 self-contained 라도 네이티브 라이브러리(Avalonia/SkiaSharp)가 단일 exe 밖으로
#    빠져, exe 하나만 배포하면 실행이 안 되고 용량도 ~6MB 작아진다(0.4.56 사고 원인).
#
# 참고: publish-all.sh 는 CLI(moai) 게시 전용이라 GUI 에는 쓰지 말 것.
set -uo pipefail
cd "$(dirname "$0")"
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

CSPROJ="src/MoaiCode.Gui/MoaiCode.Gui.csproj"
VER=$(grep -oPm1 '(?<=<Version>)[^<]+' "$CSPROJ")
[[ -z "$VER" ]] && { echo "❌ 버전을 읽지 못했습니다: $CSPROJ"; exit 1; }
OUT="dist/gui-win-x64"
ZONE="$HOME/shareHub/Zone"
DEST="$ZONE/MoAiDesktop_${VER}.exe"

echo "──── MoAI Desktop ${VER} 게시 (win-x64, self-contained 단일 exe) ────"
rm -rf "$OUT"
# UseSharedCompilation=false / nodeReuse=false : VBCSCompiler 데드락(빌드 hang) 회피.
dotnet publish "$CSPROJ" -c Release -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:UseSharedCompilation=false -nodeReuse:false \
  -o "$OUT" -clp:ErrorsOnly || { echo "❌ 게시 실패"; exit 1; }

EXE="$OUT/MoaiCode.Gui.exe"
[[ -f "$EXE" ]] || { echo "❌ 산출물 없음: $EXE"; exit 1; }

# 네이티브 미포함(옵션 누락) 감지 가드 — 정상 single-file self-contained 는 ~50MB.
SZ=$(du -m "$EXE" | cut -f1)
if [[ "$SZ" -lt 40 ]]; then
  echo "⚠️ 경고: exe 가 ${SZ}MB 로 비정상적으로 작습니다 —"
  echo "   IncludeNativeLibrariesForSelfExtract 누락 가능. 배포 중단."
  exit 1
fi

mkdir -p "$ZONE"
cp "$EXE" "$DEST"
echo "  ✅ 게시: $EXE (${SZ} MB)"
echo "📦 배포: $DEST"
