<#
.SYNOPSIS
  Downloads the pinned mihomo release used by CCNest and verifies both the archive digest published
  by GitHub and the SHA-256 of the extracted binary (the same value is pinned in MihomoBinary.cs).
#>
$ErrorActionPreference = "Stop"
$version = "v1.19.33"
$asset = "mihomo-windows-amd64-v1-$version.zip"
$zipSha = "fb0b960eff568459502d4ebe53e9cca203053d86ec9024fe935f19615d74d9aa"
$exeSha = "a4fbf2b8bc2b5416ce2d0d392ac59f2e0a234ea4fcf635b66bd21e62ea9641b9"

$root = Split-Path -Parent $PSScriptRoot
$dir = Join-Path $root "third_party\mihomo"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("ccnest-mihomo-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    $zip = Join-Path $tmp $asset
    Invoke-WebRequest "https://github.com/MetaCubeX/mihomo/releases/download/$version/$asset" -OutFile $zip -UseBasicParsing
    if ((Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $zipSha) { throw "压缩包摘要不符" }
    Expand-Archive $zip -DestinationPath $tmp
    $exe = Get-ChildItem $tmp -Filter "mihomo-windows-amd64*.exe" | Select-Object -First 1
    if ((Get-FileHash $exe.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne $exeSha) { throw "mihomo.exe 摘要不符" }
    Copy-Item $exe.FullName (Join-Path $dir "mihomo.exe") -Force
    Invoke-WebRequest "https://raw.githubusercontent.com/MetaCubeX/mihomo/$version/LICENSE" -OutFile (Join-Path $dir "LICENSE") -UseBasicParsing
    Write-Host "✓ mihomo $version 已下载并通过校验" -ForegroundColor Green
}
finally {
    $resolved = [IO.Path]::GetFullPath($tmp)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $resolved) -notmatch '^ccnest-mihomo-[0-9a-f]{32}$') {
        throw "临时目录校验失败，拒绝递归清理。"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
}
