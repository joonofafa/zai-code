#!/usr/bin/env bash
# 6개 플랫폼 전체를 self-contained 단일 파일로 크로스 게시 + SHA-256 체크섬 생성.
#   dist/<rid>/moai[.exe]            바이너리
#   dist/<rid>/moai[.exe].sha256     개별 해시
#   dist/SHASUMS256.txt              전체 통합 체크섬(고객사 배포/검증용)
#
# 서명은 별도(외부 배포는 Windows EV/Azure Trusted Signing, macOS Developer ID+notarization 필요).
# 이 해시는 고객사 화이트리스트(AppLocker/WDAC) 등록·무결성 검증용.
set -uo pipefail
cd "$(dirname "$0")"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

RIDS=(win-x64 win-x86 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64)
# 크로스-아치 R2R(crossgen)은 Linux 호스트에서 실패하므로 R2R 비활성(실행엔 지장 없음).
# --disable-build-servers: MSBuild 워커 노드 hang 방지(안정적인 반복 게시).
COMMON=(
  -c Release --self-contained true
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
  -p:EnableCompressionInSingleFile=true -p:PublishReadyToRun=false
  -p:DebugType=none -p:DebugSymbols=false
  --disable-build-servers -nodereuse:false
)

fail=0
for RID in "${RIDS[@]}"; do
  OUT="dist/${RID}"
  BIN="moai"; [[ "$RID" == win-* ]] && BIN="moai.exe"
  echo "──── ${RID} 게시 중… ────"
  rm -rf "${OUT}"
  if dotnet publish src/MoaiCode.Cli/MoaiCode.Cli.csproj -r "${RID}" "${COMMON[@]}" -o "${OUT}" -v q; then
    ( cd "${OUT}" && sha256sum "${BIN}" > "${BIN}.sha256" )
    echo "  ✅ ${OUT}/${BIN}  ($(du -h "${OUT}/${BIN}" | cut -f1))  sha256=$(cut -c1-16 "${OUT}/${BIN}.sha256")…"
  else
    echo "  ❌ ${RID} 게시 실패"; fail=1
  fi
done

# 통합 체크섬 (dist/<rid>/moai[.exe] 기준 상대경로).
echo ""
( cd dist && find . -type f \( -name moai -o -name moai.exe \) | sort \
    | xargs sha256sum > SHASUMS256.txt )
echo "📄 통합 체크섬: dist/SHASUMS256.txt"
cat dist/SHASUMS256.txt

[[ $fail -eq 0 ]] && echo "" && echo "✅ 6개 플랫폼 전체 완료" || { echo ""; echo "⚠️ 일부 실패"; exit 1; }
