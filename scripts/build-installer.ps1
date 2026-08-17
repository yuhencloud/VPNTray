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
$bundle = Join-Path $repo "dist/VPNTray-Setup-$version-x64.exe"
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

# license.rtf and the bitmaps are referenced without a path, so wix has to run from here.
Push-Location (Join-Path $repo 'installer')
try {
    Write-Host "packaging msi..."
    wix build product.wxs -ext WixToolset.UI.wixext -ext WixToolset.Util.wixext `
        -arch x64 -d "Version=$version" -o $msi
    if ($LASTEXITCODE -ne 0) { throw "wix build (msi) failed ($LASTEXITCODE)" }

    # The bundle is what people should run. It elevates itself and executes the MSI from that
    # elevated process, in the user's own session — the only context that can both see a
    # requireAdministrator app's window and be permitted to post to it, which is what lets the
    # installer close a running VPNTray. See the comment block in bundle.wxs.
    Write-Host "packaging bundle..."
    wix build bundle.wxs -ext WixToolset.BootstrapperApplications.wixext `
        -arch x64 -d "Version=$version" -d "MsiPath=$msi" -o $bundle
    if ($LASTEXITCODE -ne 0) { throw "wix build (bundle) failed ($LASTEXITCODE)" }
}
finally {
    Pop-Location
}

$fileCount = (Get-ChildItem -LiteralPath $publishDir -Recurse -File).Count
foreach ($out in @($bundle, $msi)) {
    $mb = [math]::Round((Get-Item -LiteralPath $out).Length / 1MB, 1)
    Write-Host "built $out ($mb MB)" -ForegroundColor Green
}
Write-Host "$fileCount payload files" -ForegroundColor Green
