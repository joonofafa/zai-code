#!/usr/bin/env bash
# Linux용 self-contained 단일 바이너리 게시 (런타임 미설치 환경에서 그대로 실행).
# 결과물은 dist/<rid>/moai 단일 파일 — .NET 런타임 내장, pdb 미생성.
set -euo pipefail
cd "$(dirname "$0")"

RID="${1:-linux-x64}"   # linux-x64 | linux-arm64 | linux-musl-x64 ...
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
echo "✅ 게시 완료: ${OUT}/moai"
echo "   → 이 파일 하나만 복사하면 런타임 설치 없이 실행됩니다."
