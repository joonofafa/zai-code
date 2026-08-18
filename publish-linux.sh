#!/usr/bin/env bash
# Linux용 self-contained 단일 바이너리 게시 (런타임 미설치 환경에서 그대로 실행).
# 결과물은 dist/<rid>/zaiCode 단일 파일 — .NET 런타임 내장, pdb 미생성.
#
# 안전장치: 기존 게시본은 '새 빌드가 성공한 뒤에만' 교체한다. 예전엔 시작하자마자 dist 를 지워서,
# dotnet 이 PATH 에 없어 빌드가 실패하면 멀쩡한 바이너리만 사라졌다(여러 번 당함).
set -euo pipefail
cd "$(dirname "$0")"

# dotnet 은 PATH 에 없고 사용자 홈에 설치돼 있다. 있으면 알아서 잡아 쓴다.
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

RID="${1:-linux-x64}"   # linux-x64 | linux-arm64 | linux-musl-x64 ...
OUT="dist/${RID}"
TMP="dist/.${RID}.building.$$"

cleanup() { rm -rf "${TMP}"; }
trap cleanup EXIT

rm -rf "${TMP}"
dotnet publish src/MoaiCode.Cli/MoaiCode.Cli.csproj \
  -c Release \
  -r "${RID}" \
  -f net10.0 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:PublishReadyToRun=false \
  -p:DebugType=none \
  -p:DebugSymbols=false \
  -o "${TMP}"

# 산출물 검증 — 빈 디렉토리/깨진 빌드를 기존본과 바꿔치지 않는다.
if [ ! -s "${TMP}/zaiCode" ]; then
  echo "❌ 빌드 산출물이 없습니다(${TMP}/zaiCode). 기존 게시본을 유지합니다." >&2
  exit 1
fi

# SHA-256 해시 생성 (무결성 검증용). sha256sum 표준 포맷: "<hash>  <file>".
( cd "${TMP}" && sha256sum zaiCode > zaiCode.sha256 )

# 여기까지 왔으면 성공 — 이제서야 교체한다.
rm -rf "${OUT}"
mkdir -p "$(dirname "${OUT}")"
mv "${TMP}" "${OUT}"

echo ""
echo "✅ 게시 완료: ${OUT}/zaiCode"
echo "   SHA-256: $(cut -d' ' -f1 "${OUT}/zaiCode.sha256")"
echo "   → 이 파일 하나만 복사하면 런타임 설치 없이 실행됩니다. (검증: sha256sum -c zaiCode.sha256)"
echo "   ⚠ 실행 중이던 zaiCode 세션은 재시작해야 합니다(단일 파일이 교체되어 어셈블리 로드가 깨집니다)."
