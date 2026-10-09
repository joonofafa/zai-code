# Windows에서 self-contained 단일 exe 게시.
# 결과물(zaiCode.exe)은 .NET 런타임 미설치 Windows 11에서 그대로 실행 가능.
param([string]$Rid = "win-x64")
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$out = "dist/$Rid"
# 기존 게시본은 '새 빌드가 성공한 뒤에만' 교체한다(빌드 실패 시 배포본이 사라지지 않게).
$tmp = "dist/.$Rid.building.$PID"
if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }

# Cli 는 멀티타깃(net10.0;net10.0-windows) — -f 없이 -o 를 주면 게시가 거부된다.
dotnet publish src/MoaiCode.Cli/MoaiCode.Cli.csproj `
  -c Release `
  -r $Rid `
  -f net10.0-windows `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:PublishReadyToRun=true `
  -p:DebugType=none `
  -p:DebugSymbols=false `
  -o $tmp
if ($LASTEXITCODE -ne 0 -or -not (Test-Path "$tmp/zaiCode.exe")) {
  if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
  Write-Error "빌드 산출물이 없습니다($tmp/zaiCode.exe). 기존 게시본을 유지합니다."
}

# SHA-256 해시 생성 (고객사 화이트리스트/무결성 검증용). "<hash>  <file>" 표준 포맷.
$hash = (Get-FileHash "$tmp/zaiCode.exe" -Algorithm SHA256).Hash.ToLower()
"$hash  zaiCode.exe" | Out-File -Encoding ascii "$tmp/zaiCode.exe.sha256"

# 여기까지 왔으면 성공 — 이제서야 교체한다.
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
Move-Item $tmp $out

Write-Host ""
Write-Host "OK 게시 완료: $out/zaiCode.exe"
Write-Host "   SHA-256: $hash"
Write-Host "   -> 실행 파일과 THIRD_PARTY_NOTICES.md 를 함께 복사하세요(제3자 라이선스 고지 — 배포 시 동봉 필수). 런타임 설치 없이 실행됩니다."
