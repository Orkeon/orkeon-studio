#Requires -Version 7.0
<#
.SYNOPSIS
  Builds the per-user Orkeon Studio MSI from the win-x64 tree of package-studio.sh,
  with the WiX Toolset.
.DESCRIPTION
  Harvests installers\msi\Package.wxs against -StageDir (bin\, libexec\orkeon-studio\,
  licenses\, README.md, LICENSE.md, NOTICE.md, THIRD-PARTY-NOTICES.md, VERSION: the tree inside
  orkeon-studio-<ver>-win-x64.zip) and writes orkeon-studio-<ver>-win-x64.msi plus a
  SHA256SUMS.msi line next to it. SHA256SUMS.msi is separate from SHA256SUMS: that one
  is written on the Linux packaging job, before this MSI exists (see release.yml).

  Adapted from Orkeon's scripts/build-msi.ps1. WiX is pinned to v5 in
  .config\dotnet-tools.json: v6 and later refuse to run until the Open Source
  Maintenance Fee EULA is accepted, which an unattended build must not do on the
  project's behalf.
.PARAMETER StageDir
  The extracted zip's single top-level folder (the one holding bin\ and libexec\).
.PARAMETER Version
  Full version string. Default: StageDir\VERSION. The MSI ProductVersion takes no
  suffix, so it is truncated to x.y.z; the full string stays in the file name and in
  ARPCOMMENTS.
.PARAMETER OutDir
  Output directory. Default: artifacts\release.
.EXAMPLE
  pwsh scripts/build-msi.ps1 -StageDir artifacts\_extract\orkeon-studio-0.0.0-win-x64
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$StageDir,
    [string]$Version = '',
    [string]$OutDir = ''
)
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$MsiSrc = Join-Path $RepoRoot 'installers\msi'
if (-not $OutDir) { $OutDir = Join-Path $RepoRoot 'artifacts\release' }
# Absolute before Push-Location: the checksum below runs after Pop-Location.
if (-not [System.IO.Path]::IsPathRooted($OutDir)) { $OutDir = Join-Path $RepoRoot $OutDir }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path -LiteralPath $OutDir).Path

$StageDir = (Resolve-Path -LiteralPath $StageDir).Path
$studioExe = Join-Path $StageDir 'libexec\orkeon-studio\Orkeon.Studio.exe'
if (-not (Test-Path -LiteralPath $studioExe)) {
    throw "StageDir '$StageDir' has no libexec\orkeon-studio\Orkeon.Studio.exe -- pass the zip's inner folder, not its extraction root."
}

if (-not $Version) {
    $versionFile = Join-Path $StageDir 'VERSION'
    if (Test-Path -LiteralPath $versionFile) { $Version = (Get-Content -LiteralPath $versionFile -Raw).Trim() }
}
if (-not $Version) { throw 'Could not resolve a version; pass -Version or ensure VERSION exists in StageDir.' }
if ($Version -notmatch '^(\d+\.\d+\.\d+)') { throw "Version '$Version' does not start with x.y.z." }
$MsiVersion = $Matches[1]

$manifest = Get-Content -LiteralPath (Join-Path $RepoRoot '.config\dotnet-tools.json') -Raw | ConvertFrom-Json
$WixVersion = $manifest.tools.wix.version
if (-not $WixVersion) { throw 'No tools.wix.version in .config\dotnet-tools.json' }

Push-Location $RepoRoot
try {
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed' }

    dotnet tool run wix -- extension add --global "WixToolset.UI.wixext/$WixVersion"
    if ($LASTEXITCODE -ne 0) { throw 'wix extension add WixToolset.UI.wixext failed' }

    $msiName = "orkeon-studio-$Version-win-x64.msi"
    $msiPath = Join-Path $OutDir $msiName
    if (Test-Path -LiteralPath $msiPath) { Remove-Item -LiteralPath $msiPath -Force }

    # -bindpath: WixUILicenseRtf names License.rtf with no folder, resolved against the
    # bind paths before the working directory.
    dotnet tool run wix -- build `
        -arch x64 `
        -d "StudioVersion=$MsiVersion" `
        -d "StudioVersionFull=$Version" `
        -d "PublishDir=$StageDir" `
        -ext WixToolset.UI.wixext `
        -bindpath $MsiSrc `
        -o $msiPath `
        (Join-Path $MsiSrc 'Package.wxs')
    if ($LASTEXITCODE -ne 0) { throw 'wix build failed' }
    if (-not (Test-Path -LiteralPath $msiPath)) { throw "wix build reported success but $msiPath is missing" }
} finally {
    Pop-Location
}

# LF-terminated: `sha256sum -c` on Linux would read a trailing CR as part of the name.
$hash = (Get-FileHash $msiPath -Algorithm SHA256).Hash.ToLower()
$sumsLine = '{0}  {1}' -f $hash, $msiName
[IO.File]::WriteAllText((Join-Path $OutDir 'SHA256SUMS.msi'), "$sumsLine`n")
Write-Host "==> Done. $msiPath"
Write-Host "    $sumsLine"
