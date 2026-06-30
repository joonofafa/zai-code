#!/usr/bin/env bash
# Linux/macOS에서 Windows용 self-contained 단일 exe 크로스 게시.
# 결과물(moai.exe)은 .NET 런타임 미설치 Windows 11에서 그대로 실행 가능.
set -euo pipefail
cd "$(dirname "$0")"

RID="${1:-win-x64}"
OUT="dist/${RID}"

rm -rf "${OUT}"

dotnet publish src/MoaiCode.Cli/MoaiCode.Cli.csproj \
  -c Release \
  -r "${RID}" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:PublishReadyToRun=true \
  -p:DebugType=none \
  -p:DebugSymbols=false \
  -o "${OUT}"

echo ""
echo "✅ 게시 완료: ${OUT}/moai.exe"
echo "   → 이 파일 하나만 Windows 11로 복사하면 런타임 설치 없이 실행됩니다."
