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
  -p:PublishReadyToRun=false \
  -p:DebugType=none \
  -p:DebugSymbols=false \
  -o "${OUT}"

# SHA-256 해시 생성 (고객사 화이트리스트/무결성 검증용). sha256sum 표준 포맷: "<hash>  <file>".
( cd "${OUT}" && sha256sum moai > moai.sha256 )

echo ""
echo "✅ 게시 완료: ${OUT}/moai"
echo "   SHA-256: $(cut -d' ' -f1 "${OUT}/moai.sha256")"
echo "   → 이 파일 하나만 복사하면 런타임 설치 없이 실행됩니다. (검증: sha256sum -c moai.sha256)"
