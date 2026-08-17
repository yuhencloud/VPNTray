<#
.SYNOPSIS
    Publishes VPNTray self-contained and packages it into dist/VPNTray-Setup-<version>-x64.msi.

.DESCRIPTION
    The version is read from src/VPNTray/VPNTray.csproj and used for three things — the
    assembly stamp, the MSI's ProductVersion, and the output file name. Bump <Version>
    there and nowhere else; product.wxs takes it via -d and this script names the file.

    Two numbers for one release is how a bug report ends up filed against a build nobody
    can identify.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $repo 'src/VPNTray/VPNTray.csproj'
$publishDir = Join-Path $repo 'dist/VPNTray'

$version = ([xml](Get-Content -LiteralPath $csproj)).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> element in $csproj" }

$msi = Join-Path $repo "dist/VPNTray-Setup-$version-x64.msi"
Write-Host "VPNTray $version" -ForegroundColor Cyan

# Wiped, not overwritten: product.wxs harvests dist/VPNTray/** wholesale, so anything an
# earlier publish left behind would ship inside the MSI with nothing to flag it.
if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

Write-Host "publishing..."
dotnet publish $csproj -c $Configuration -r win-x64 --self-contained true `
    -p:Platform=x64 -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# license.rtf is referenced without a path, so wix has to run from the installer directory.
Push-Location (Join-Path $repo 'installer')
try {
    Write-Host "packaging..."
    wix build product.wxs -ext WixToolset.UI.wixext -arch x64 -d "Version=$version" -o $msi
    if ($LASTEXITCODE -ne 0) { throw "wix build failed ($LASTEXITCODE)" }
}
finally {
    Pop-Location
}

$sizeMb = [math]::Round((Get-Item -LiteralPath $msi).Length / 1MB, 1)
$fileCount = (Get-ChildItem -LiteralPath $publishDir -Recurse -File).Count
Write-Host "built $msi ($sizeMb MB, $fileCount payload files)" -ForegroundColor Green
