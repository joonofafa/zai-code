#!/usr/bin/env bash
# 6개 플랫폼 전체를 self-contained 단일 파일로 크로스 게시 + SHA-256 체크섬 생성.
#   dist/<rid>/zaiCode[.exe]            바이너리
#   dist/<rid>/zaiCode[.exe].sha256     개별 해시
#   dist/SHASUMS256.txt              전체 통합 체크섬(고객사 배포/검증용)
#
# 서명은 별도(외부 배포는 Windows EV/Azure Trusted Signing, macOS Developer ID+notarization 필요).
# 이 해시는 고객사 화이트리스트(AppLocker/WDAC) 등록·무결성 검증용.
set -uo pipefail
cd "$(dirname "$0")"

# dotnet 은 PATH 에 없고 사용자 홈에 설치돼 있다. 있으면 알아서 잡아 쓴다.
# (없는데도 진행하면 전 플랫폼 게시본이 통째로 사라진다 — 실제로 당한 사고.)
if ! command -v dotnet >/dev/null 2>&1; then
  if [ -x "$HOME/.dotnet/dotnet" ]; then
    export DOTNET_ROOT="$HOME/.dotnet"
    export PATH="$DOTNET_ROOT:$PATH"
  else
    echo "❌ dotnet 을 찾을 수 없습니다 (PATH 에도, $HOME/.dotnet 에도 없음)." >&2
    echo "   기존 게시본은 건드리지 않고 중단합니다." >&2
    exit 1
  fi
fi
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

# 중단(Ctrl-C 등)돼도 이번 실행이 만든 임시 디렉터리는 남기지 않는다 — 통합 체크섬에 섞여 든다.
cleanup() { rm -rf dist/.*.building.$$; }
trap cleanup EXIT

fail=0
for RID in "${RIDS[@]}"; do
  OUT="dist/${RID}"
  BIN="zaiCode"; [[ "$RID" == win-* ]] && BIN="zaiCode.exe"
  # Cli 는 멀티타깃(net10.0;net10.0-windows) — win-* 는 Windows 타깃(Office COM 툴 포함), 나머지는 net10.0.
  TFM="net10.0"; [[ "$RID" == win-* ]] && TFM="net10.0-windows"
  TMP="dist/.${RID}.building.$$"
  echo "──── ${RID} (${TFM}) 게시 중… ────"
  # 기존 게시본은 '새 빌드가 성공한 뒤에만' 교체한다. 실패한 플랫폼은 이전 산출물이 그대로 남는다.
  rm -rf "${TMP}"
  if dotnet publish src/MoaiCode.Cli/MoaiCode.Cli.csproj -r "${RID}" -f "${TFM}" "${COMMON[@]}" -o "${TMP}" -v q \
     && [ -s "${TMP}/${BIN}" ]; then
    ( cd "${TMP}" && sha256sum "${BIN}" > "${BIN}.sha256" )
    rm -rf "${OUT}"; mv "${TMP}" "${OUT}"
    echo "  ✅ ${OUT}/${BIN}  ($(du -h "${OUT}/${BIN}" | cut -f1))  sha256=$(cut -c1-16 "${OUT}/${BIN}.sha256")…"
  else
    rm -rf "${TMP}"
    echo "  ❌ ${RID} 게시 실패 — 기존 ${OUT} 유지"; fail=1
  fi
done

# 통합 체크섬 (dist/<rid>/zaiCode[.exe] 기준 상대경로).
echo ""
( cd dist && find . -type f \( -name zaiCode -o -name zaiCode.exe \) | sort \
    | xargs sha256sum > SHASUMS256.txt )
echo "📄 통합 체크섬: dist/SHASUMS256.txt"
cat dist/SHASUMS256.txt

[[ $fail -eq 0 ]] && echo "" && echo "✅ 6개 플랫폼 전체 완료" || { echo ""; echo "⚠️ 일부 실패"; exit 1; }
