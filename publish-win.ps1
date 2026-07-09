# Windows에서 self-contained 단일 exe 게시.
# 결과물(moai.exe)은 .NET 런타임 미설치 Windows 11에서 그대로 실행 가능.
param([string]$Rid = "win-x64")
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$out = "dist/$Rid"

if (Test-Path $out) { Remove-Item -Recurse -Force $out }

dotnet publish src/MoaiCode.Cli/MoaiCode.Cli.csproj `
  -c Release `
  -r $Rid `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:PublishReadyToRun=true `
  -p:DebugType=none `
  -p:DebugSymbols=false `
  -o $out

# SHA-256 해시 생성 (고객사 화이트리스트/무결성 검증용). "<hash>  <file>" 표준 포맷.
$hash = (Get-FileHash "$out/moai.exe" -Algorithm SHA256).Hash.ToLower()
"$hash  moai.exe" | Out-File -Encoding ascii "$out/moai.exe.sha256"

Write-Host ""
Write-Host "OK 게시 완료: $out/moai.exe"
Write-Host "   SHA-256: $hash"
Write-Host "   -> 이 파일 하나만 복사하면 런타임 설치 없이 실행됩니다."
