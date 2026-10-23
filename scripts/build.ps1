<#
.SYNOPSIS
  Builds, tests and packages CCNest.

.DESCRIPTION
  1. Verifies the pinned mihomo binary (downloads it with -FetchCore if missing).
  2. Runs the test suite (skip with -SkipTests).
  3. Publishes a self-contained win-x64 build to artifacts\publish\CCNest.
  4. Produces a portable zip and, when Inno Setup's ISCC.exe is available, the installer.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\build.ps1
#>
param(
    [string]$Version = "1.0.0",
    [switch]$SkipTests,
    [switch]$FetchCore,
    [string]$Iscc = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$coreDir = Join-Path $root "third_party\mihomo"
$coreExe = Join-Path $coreDir "mihomo.exe"
$pinnedSha = "a4fbf2b8bc2b5416ce2d0d392ac59f2e0a234ea4fcf635b66bd21e62ea9641b9"

if (-not (Test-Path $coreExe)) {
    if (-not $FetchCore) { throw "third_party\mihomo\mihomo.exe 不存在。请使用 -FetchCore 下载，或运行 scripts\fetch-mihomo.ps1。" }
    & (Join-Path $PSScriptRoot "fetch-mihomo.ps1")
}
$actual = (Get-FileHash $coreExe -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $pinnedSha) { throw "mihomo.exe 的 SHA-256 ($actual) 与固定值不符，拒绝打包。" }
Write-Host "✓ mihomo 内核校验通过" -ForegroundColor Green

if (-not $SkipTests) {
    dotnet test tests\CCNest.Tests\CCNest.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw "测试失败" }
}

$publish = Join-Path $root "artifacts\publish\CCNest"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish src\CCNest.App\CCNest.App.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:DebugType=none -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw "发布失败" }
Write-Host "✓ 已发布到 $publish" -ForegroundColor Green

$installerDir = Join-Path $root "artifacts\installer"
New-Item -ItemType Directory -Force -Path $installerDir | Out-Null
$zip = Join-Path $installerDir "CCNest-$Version-win-x64-portable.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$publish\*" -DestinationPath $zip -CompressionLevel Optimal
Write-Host "✓ 便携版：$zip" -ForegroundColor Green

if (-not $Iscc) {
    $candidates = @(
        (Join-Path $root ".tools\innosetup-pkg\pkg\tools\ISCC.exe"),
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    $Iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if ($Iscc) {
    & $Iscc "/DAppVersion=$Version" "/DPublishDir=$publish" (Join-Path $root "installer\CCNest.iss")
    if ($LASTEXITCODE -ne 0) { throw "安装程序生成失败" }
    Write-Host "✓ 安装程序已生成到 $installerDir" -ForegroundColor Green
} else {
    Write-Warning "未找到 Inno Setup (ISCC.exe)，仅生成了便携版 zip。"
}
