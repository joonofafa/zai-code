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

# 여기까지 왔으면 성공 — 교체한다. 단, 실행 중인 옛 세션을 깨뜨리지 않게 inode 를 보존한다:
# 단일 파일 바이너리는 어셈블리를 lazy 로드하므로, 실행 파일을 지우고 새로 놓으면 이미 떠 있는
# 프로세스의 이후 어셈블리 로드(예: 첫 Bash 호출 시 CliWrap)가 'Could not load file or assembly'
# 로 실패한다(세션 내 자가복구 불가). 따라서 버전 디렉터리에 보관하고 zaiCode 는 심볼릭 링크로
# 가리킨다 — 링크 교체는 옛 inode 를 건드리지 않아 옛 세션이 계속 옛 파일을 읽는다.
STAMP="$(date +%Y%m%d-%H%M%S)"
VERSIONED="dist/versions/${RID}/${STAMP}-$$"
mkdir -p "$(dirname "${VERSIONED}")"
rm -rf "${OUT}"   # 옛 링크/디렉터리 제거(있으면)
mkdir -p "${OUT}"
mv "${TMP}" "${VERSIONED}"
# 링크는 ${OUT}/zaiCode 파일이어야 런처 경로(dist/<rid>/zaiCode)가 유지된다. 상대경로는
# 링크 위치(dist/<rid>/) 기준으로 해석되므로 ../ 로 dist/versions 를 가리킨다.
ln -s "../versions/${RID}/${STAMP}-$$/zaiCode" "${OUT}/zaiCode"

# 오래된 버전 보관본 정리(최근 5개만 유지 — 옛 세션이 잡고 있는 inode 는 unlink 로도 안전).
ls -1dt "$(dirname "${VERSIONED}")"/[0-9]*-* 2>/dev/null | tail -n +6 | xargs -r rm -rf

# 검증: 링크가 실제 실행 파일을 가리키는지.
if [ ! -x "${OUT}/zaiCode" ]; then
  echo "❌ 게시 검증 실패: ${OUT}/zaiCode 가 실행 파일이 아닙니다." >&2
  exit 1
fi

echo ""
echo "✅ 게시 완료: ${OUT}/zaiCode -> versions/${RID}/${STAMP}-$$/zaiCode"
echo "   SHA-256: $(cut -d' ' -f1 "${VERSIONED}/zaiCode.sha256")"
echo "   → 이 파일 하나만 복사하면 런타임 설치 없이 실행됩니다. (검증: sha256sum -c zaiCode.sha256)"
echo "   ⚠ 심볼릭 링크 교체 방식이라 기존 세션도 계속 동작하지만, 새 기능은 재시작해야 씁니다."
