<#
.SYNOPSIS
  Installs Orkeon Studio from this clone, next to an Orkeon CLI (Windows;
  scripts/install-from-source.sh is the same contract for Linux and macOS).
.DESCRIPTION
    git clone https://github.com/Orkeon/orkeon.git
    git clone https://github.com/Orkeon/orkeon-studio.git
    cd orkeon-studio
    pwsh -File .\scripts\install-from-source.ps1 -WithOrkeon

  What it does, in this order:
    1. checks what the build needs -- every missing tool is named at once, before
       anything is compiled -- and finds the Orkeon checkout (-OrkeonSrc, else
       ORKEON_SOURCE_ROOT, else ..\orkeon);
    2. with -WithOrkeon, installs the Orkeon CLI from that checkout first, with the
       checkout's own scripts\install-from-source.ps1;
    3. packs the Orkeon packages the Studio apps compile against into orkeon-feed\, at
       a version dated by the checkout's last commit (scripts\resolve-version.ps1),
       unless the feed already holds them: a new Orkeon commit gives a new version, and
       no cache is ever cleared. Two commits of the same minute share a version, and
       NuGet keeps the first copy it extracted: the script refuses to pack a checkout
       with uncommitted changes, or to reuse a feed packed from another commit;
    4. publishes the Studio apps -- orkeon-studio (WPF), orkeon-studio-config and
       orkeon-studio-run -- into <InstallDir>\libexec, next to the CLI, where Studio
       looks for it first; writes their launchers into <InstallDir>\bin, which the CLI's
       installer put on the user PATH; adds an "Orkeon Studio" Start menu shortcut.

  Studio lives inside the CLI's installation, and the CLI's install.ps1 replaces that
  directory whole: after reinstalling the CLI, run this script again. With -WithOrkeon,
  the CLI's own installer also publishes the Studio copy its checkout carries, which
  this script then replaces. The CLI's
  uninstall does not know Studio: run this script with -Uninstall first, or the Start
  menu shortcut is left pointing at nothing. A CLI installed by the MSI is refused:
  its files belong to Windows Installer.

  This file is written for Windows PowerShell 5.1 as well as PowerShell 7, and stays
  ASCII, so that under 5.1 it gets as far as saying the build needs PowerShell 7.
.PARAMETER OrkeonSrc
  The Orkeon checkout to pack (and, with -WithOrkeon, to install).
.PARAMETER WithOrkeon
  Installs the Orkeon CLI from -OrkeonSrc before Studio.
.PARAMETER InstallDir
  Where the CLI is installed. Default: %LOCALAPPDATA%\Programs\Orkeon.
.PARAMETER SelfContained
  Bundles the .NET runtime with each app (the first run downloads the runtime pack
  once). Default: the apps use the .NET 10 runtime of the machine, which the SDK this
  build needs already provides -- found where .NET is installed for the machine, or
  through DOTNET_ROOT for an SDK installed elsewhere (dotnet-install, say).
.PARAMETER Uninstall
  Removes the Studio apps this script installed in -InstallDir and leaves the CLI.
  Builds nothing.
.PARAMETER WhatIf
  Checks the prerequisites, prints the versions and the commands it would run, and
  stops there.
.NOTES
  Exit: 0 installed (or would be, with -WhatIf); 1 a prerequisite is missing, the
  build or the install failed, or there is nothing to uninstall.
#>
[CmdletBinding()]
param(
    [string]$OrkeonSrc = '',
    [switch]$WithOrkeon,
    [string]$InstallDir = '',
    [switch]$SelfContained,
    [switch]$Uninstall,
    [switch]$WhatIf
)
$ErrorActionPreference = 'Stop'

function Write-Problem([string]$Message) {
    [Console]::Error.WriteLine($Message)
}

# --- Which PowerShell ---------------------------------------------------------------
if ($PSVersionTable.PSVersion.Major -lt 7) {
    Write-Problem "install-from-source: building Orkeon Studio needs PowerShell 7 (pwsh). This is Windows PowerShell $($PSVersionTable.PSVersion)."
    Write-Problem '  Get PowerShell 7 from https://aka.ms/powershell-release?tag=stable  (or: winget install --id Microsoft.PowerShell)'
    Write-Problem '  then, from this clone:  pwsh -File .\scripts\install-from-source.ps1'
    exit 1
}

if (-not $IsWindows) {
    Write-Problem 'install-from-source: this script installs on Windows. On Linux and macOS, run scripts/install-from-source.sh.'
    exit 1
}

$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $InstallDir) { $InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\Orkeon' }
if (-not $OrkeonSrc) {
    if ($env:ORKEON_SOURCE_ROOT) { $OrkeonSrc = $env:ORKEON_SOURCE_ROOT } else { $OrkeonSrc = Join-Path $RepoRoot '..\orkeon' }
}
# Relative paths are read against the PowerShell location, not the process directory.
$OrkeonSrc = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OrkeonSrc)
$InstallDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($InstallDir)

$binDir = Join-Path $InstallDir 'bin'
$libexecDir = Join-Path $InstallDir 'libexec'
$feed = Join-Path $RepoRoot 'orkeon-feed'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Orkeon Studio.lnk'
# A framework-dependent app needs an apphost of the machine's architecture.
$rid = if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [Runtime.InteropServices.Architecture]::Arm64) { 'win-arm64' } else { 'win-x64' }

# What this script installs: launcher name, project, apphost.
$apps = @(
    @{ Name = 'orkeon-studio';        Project = 'src\apps\Orkeon.Studio.Wpf\Orkeon.Studio.Wpf.csproj';       AppHost = 'Orkeon.Studio' },
    @{ Name = 'orkeon-studio-config'; Project = 'src\apps\Orkeon.Studio.Config\Orkeon.Studio.Config.csproj'; AppHost = 'Orkeon.Studio.Config' },
    @{ Name = 'orkeon-studio-run';    Project = 'src\apps\Orkeon.Studio.Run\Orkeon.Studio.Run.csproj';       AppHost = 'Orkeon.Studio.Run' }
)

# The Orkeon packages the apps restore and the project each is packed from: the same list
# as install-from-source.sh (Orkeon.Tools and Orkeon.Scripting serve the tests alone).
$packages = @(
    @{ Id = 'Orkeon';                        Project = 'src\packaging\Orkeon\Orkeon.csproj' },
    @{ Id = 'Orkeon.Constants.Cli';          Project = 'src\constants\Orkeon.Constants.Cli\Orkeon.Constants.Cli.csproj' },
    @{ Id = 'Orkeon.Cli.Abstractions';       Project = 'src\cli\Orkeon.Cli.Abstractions\Orkeon.Cli.Abstractions.csproj' },
    @{ Id = 'Orkeon.Cli.TerminalGui';        Project = 'src\packaging\Orkeon.Cli.TerminalGui\Orkeon.Cli.TerminalGui.Package.csproj' },
    @{ Id = 'Orkeon.Compliance.Vfs';         Project = 'src\analyzers\Orkeon.Compliance.Vfs\Orkeon.Compliance.Vfs.csproj' }
)

$launcherMark = "written by orkeon-studio's scripts\install-from-source.ps1"

function Remove-StudioApp([string]$Name) {
    $cmd = Join-Path $binDir "$Name.cmd"
    $dir = Join-Path $libexecDir $Name
    if (Test-Path -LiteralPath $cmd) { Remove-Item -LiteralPath $cmd -Force }
    if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
}

# The per-user MSI installs into the same directory: its files belong to Windows Installer,
# which would repair or remove them behind this script (same test as the CLI's install.ps1).
function Test-MsiChannelInstalled {
    $uninstallRoot = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall'
    if (-not (Test-Path -LiteralPath $uninstallRoot)) { return $false }
    $guidPattern = '^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$'
    $productKeys = Get-ChildItem -LiteralPath $uninstallRoot -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -match $guidPattern }
    foreach ($productKey in $productKeys) {
        $displayName = (Get-ItemProperty -LiteralPath $productKey.PSPath -ErrorAction SilentlyContinue).DisplayName
        if ($displayName -eq 'Orkeon') { return $true }
    }
    return $false
}

# Windows does not let a running executable be deleted: a recursive delete would stop on
# the first locked file, half-way through an app. Refusing first changes nothing.
function Assert-StudioNotRunning {
    $running = @()
    foreach ($app in $apps) {
        $dir = Join-Path $libexecDir $app.Name
        if (-not (Test-Path -LiteralPath $dir)) { continue }
        $prefix = [IO.Path]::GetFullPath($dir).TrimEnd('\') + '\'
        foreach ($process in @(Get-Process -ErrorAction SilentlyContinue)) {
            $path = $null
            try { $path = $process.Path } catch { $path = $null }
            if ($path -and $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { $running += $process }
        }
    }
    if ($running.Count -gt 0) {
        $names = ($running | ForEach-Object { "$($_.ProcessName) (PID $($_.Id))" }) -join ', '
        Write-Problem "install-from-source: Orkeon Studio is running from ${libexecDir}: $names. Close it, then run this again. Nothing was changed."
        exit 1
    }
}

$msiDir = Join-Path $env:LOCALAPPDATA 'Programs\Orkeon'
if ($InstallDir.TrimEnd('\') -ieq $msiDir -and (Test-MsiChannelInstalled)) {
    Write-Problem "install-from-source: Orkeon is installed in $InstallDir by its MSI (see 'Orkeon' in Add/Remove Programs), which already carries Studio. Uninstall it first (-WithOrkeon then installs the CLI from source), or name another -InstallDir."
    exit 1
}

# --- Uninstall: nothing to build ------------------------------------------------------
if ($Uninstall) {
    # Only what this script wrote goes: the CLI's archives carry a Studio of their own,
    # whose launcher does not bear this script's mark.
    $found = @($apps | Where-Object {
        $cmd = Join-Path $binDir "$($_.Name).cmd"
        (Test-Path -LiteralPath $cmd) -and (Select-String -LiteralPath $cmd -SimpleMatch $launcherMark -Quiet)
    })
    if ($found.Count -eq 0) {
        Write-Problem "Nothing to uninstall: no Orkeon Studio app installed by this script in $InstallDir."
        exit 1
    }
    if ($WhatIf) {
        foreach ($app in $found) { Write-Host "Would remove $($app.Name) from $InstallDir" }
        Write-Host "Would remove $shortcut and $(Join-Path $InstallDir 'STUDIO-VERSION'), if present"
        exit 0
    }
    Assert-StudioNotRunning
    foreach ($app in $found) { Remove-StudioApp $app.Name }
    if (-not $WhatIf) {
        if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut -Force }
        $versionFile = Join-Path $InstallDir 'STUDIO-VERSION'
        if (Test-Path -LiteralPath $versionFile) { Remove-Item -LiteralPath $versionFile -Force }
        Write-Host "Orkeon Studio uninstalled from $InstallDir (the CLI stays)."
    }
    exit 0
}

# --- Prerequisites, all of them, before anything is compiled ------------------------------
function Get-NativeExitCode([scriptblock]$Command) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $Command *> $null
        return $LASTEXITCODE
    } catch {
        return -1
    } finally {
        $ErrorActionPreference = $previous
    }
}

$sdkWanted = '10.0.300'
try {
    $pinned = (Get-Content -LiteralPath (Join-Path $RepoRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
    if ($pinned) { $sdkWanted = [string]$pinned }
} catch {
    $sdkWanted = '10.0.300'
}
$sdkLine = ($sdkWanted -split '\.')[0..1] -join '.'
$sdkPage = "https://dotnet.microsoft.com/download/dotnet/$sdkLine"

$missing = New-Object System.Collections.Generic.List[string]

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    $missing.Add("the .NET SDK $sdkWanted or a later $sdkLine.x (global.json pins it) -- $sdkPage")
} else {
    Push-Location -LiteralPath $RepoRoot
    try { $sdkFits = (Get-NativeExitCode { dotnet --version }) -eq 0 } finally { Pop-Location }
    if (-not $sdkFits) {
        $missing.Add("a .NET SDK that satisfies global.json: $sdkWanted or a later $sdkLine.x -- $sdkPage")
    }
}
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    $missing.Add('git, which dates the versions of this build -- https://git-scm.com/download/win')
}
if (-not (Test-Path -LiteralPath (Join-Path $OrkeonSrc 'src\packaging\Orkeon\Orkeon.csproj'))) {
    $missing.Add("an Orkeon checkout at $OrkeonSrc (-OrkeonSrc or ORKEON_SOURCE_ROOT name another): git clone --depth 1 --filter=blob:none https://github.com/Orkeon/orkeon.git")
} elseif (-not (Test-Path -LiteralPath (Join-Path $OrkeonSrc 'src\packaging\Orkeon.Cli.TerminalGui\Orkeon.Cli.TerminalGui.Package.csproj'))) {
    # Before the packaging that ships the CLI family to NuGet, three of the packages Studio
    # needs cannot be packed at all.
    $missing.Add("an Orkeon checkout that packages the CLI family for Studio (src\packaging\Orkeon.Cli.TerminalGui): $OrkeonSrc predates it -- check out a commit that has it")
}
$cliExe = Join-Path $libexecDir 'orkeon\orkeon.exe'
if (-not $WithOrkeon -and -not (Test-Path -LiteralPath $cliExe)) {
    $missing.Add("an Orkeon CLI installed in $InstallDir ($cliExe): add -WithOrkeon to install it from $OrkeonSrc first")
}

if ($missing.Count -gt 0) {
    Write-Problem "install-from-source: $($missing.Count) thing(s) the build needs are missing. Nothing was built."
    foreach ($item in $missing) { Write-Problem "  - $item" }
    Write-Problem 'Provide what is listed, then run this script again. It installs none of it for you.'
    exit 1
}

# --- Versions ----------------------------------------------------------------------------
$studioVersion = & (Join-Path $PSScriptRoot 'resolve-version.ps1') -Props (Join-Path $RepoRoot 'src\Directory.Build.props')
$studioOk = $LASTEXITCODE -eq 0
$orkeonVersion = & (Join-Path $OrkeonSrc 'scripts\resolve-version.ps1') -Props (Join-Path $OrkeonSrc 'src\Directory.Build.props')
if (-not $studioOk -or $LASTEXITCODE -ne 0 -or -not $studioVersion -or -not $orkeonVersion) {
    Write-Problem 'install-from-source: resolve-version.ps1 gave no version (see its message above).'
    exit 1
}
$studioVersion = ([string]$studioVersion).Trim()
$orkeonVersion = ([string]$orkeonVersion).Trim()

function Test-FeedComplete {
    foreach ($package in $packages) {
        if (-not (Test-Path -LiteralPath (Join-Path $feed "$($package.Id).$orkeonVersion.nupkg"))) { return $false }
    }
    return $true
}

Write-Host "Orkeon Studio $studioVersion, from the sources in $RepoRoot ($rid),"
Write-Host "against Orkeon $orkeonVersion from $OrkeonSrc."

# The version is the minute of the Orkeon commit: uncommitted changes, or another commit of
# the same minute, do not move it, and NuGet never extracts a version twice. The feed
# records the commit it was packed from, and a pack only ever sees a committed tree.
$previous = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    $dirty = @(git -C $OrkeonSrc status --porcelain --untracked-files=no --ignore-submodules 2>$null)
    $orkeonHead = [string](git -C $OrkeonSrc rev-parse HEAD 2>$null)
} finally { $ErrorActionPreference = $previous }
$orkeonHead = $orkeonHead.Trim()
$feedMark = Join-Path $feed ".orkeon-$orkeonVersion.commit"
if (Test-FeedComplete) {
    $packedFrom = if (Test-Path -LiteralPath $feedMark) { (Get-Content -LiteralPath $feedMark -Raw).Trim() } else { '' }
    if ($packedFrom -and $orkeonHead -and $packedFrom -ne $orkeonHead) {
        Write-Problem "install-from-source: orkeon-feed holds Orkeon $orkeonVersion packed from commit $packedFrom,"
        Write-Problem "  but $OrkeonSrc is at $orkeonHead, which carries the same version. Commit again (a new"
        Write-Problem "  minute gives a new version), or check out $packedFrom. Nothing was built."
        exit 1
    }
    if ($dirty.Count -gt 0) {
        Write-Host "Note: $OrkeonSrc has uncommitted changes; this build uses the packages of its last commit, without them."
    }
} elseif ($dirty.Count -gt 0) {
    Write-Problem "install-from-source: $OrkeonSrc has uncommitted changes. Packed, they would carry the version"
    Write-Problem "  of its last commit, $orkeonVersion, for good. Commit or stash them, then run this again."
    exit 1
}

$orkeonInstaller = Join-Path $OrkeonSrc 'scripts\install-from-source.ps1'
# NU1603 is the restore settling for a higher version than the one asked when that one is
# missing: 1.0.0-studio.local sorts above every 1.0.0-rc.N.local.* stamp, so that would be
# a Studio built against the wrong Orkeon.
$versionProps = @("-p:OrkeonVersion=$orkeonVersion", '-p:NuGetAudit=false', '-warnaserror:NU1603')
$stage = Join-Path $RepoRoot "artifacts\install-from-source\$rid"
# StudioBuildRoot (Directory.Build.props) keeps this build's obj\ and bin\ under the stage,
# which is emptied first: restored with another OrkeonVersion, it must not leave its assets
# file in the projects. The Orkeon packages may bring one file twice, as the Orkeon
# installers' own publish of Studio allows.
$publishProps = @("-p:StudioBuildRoot=$(Join-Path $stage 'build')", '-p:PublishTrimmed=false', '-p:ErrorOnDuplicatePublishOutputFiles=false')
$selfContainedArg = if ($SelfContained) { 'true' } else { 'false' }

if ($WhatIf) {
    Write-Host 'Prerequisites: all present. -WhatIf: nothing is built, nothing is installed. Would run:'
    if ($WithOrkeon) { Write-Host "  $orkeonInstaller -InstallDir `"$InstallDir`"" }
    if (Test-FeedComplete) {
        Write-Host "  (orkeon-feed already holds the packages at ${orkeonVersion}: no pack)"
    } else {
        foreach ($package in $packages) {
            Write-Host "  dotnet pack $(Join-Path $OrkeonSrc $package.Project) -c Release -p:Version=$orkeonVersion -p:NuGetAudit=false -o $feed --nologo -v quiet"
        }
    }
    foreach ($app in $apps) {
        Write-Host "  dotnet publish $($app.Project) -c Release -r $rid --self-contained $selfContainedArg -p:Version=$studioVersion $($versionProps -join ' ') $($publishProps -join ' ') -o $(Join-Path $stage $app.Name)"
        Write-Host "    then copied to $(Join-Path $libexecDir $app.Name), launcher $(Join-Path $binDir "$($app.Name).cmd")"
    }
    exit 0
}

# --- The CLI -------------------------------------------------------------------------------
if ($WithOrkeon) {
    Write-Host ''
    Write-Host "==> Installing the Orkeon CLI from $OrkeonSrc"
    # A script that ends without 'exit' leaves $LASTEXITCODE as it found it.
    $global:LASTEXITCODE = 0
    try {
        & $orkeonInstaller -InstallDir $InstallDir
    } catch {
        Write-Problem "install-from-source: the Orkeon CLI installer stopped: $($_.Exception.Message)"
        exit 1
    }
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $cliExe)) {
        Write-Problem "install-from-source: the Orkeon CLI did not install (its message is above; expected $cliExe). Studio was not built."
        exit 1
    }
}

# --- The feed ------------------------------------------------------------------------------
Write-Host ''
if (Test-FeedComplete) {
    Write-Host "==> orkeon-feed already holds the Orkeon packages at $orkeonVersion"
} else {
    Write-Host "==> Packing the Orkeon packages at $orkeonVersion into orkeon-feed"
    New-Item -ItemType Directory -Force -Path $feed | Out-Null
    foreach ($package in $packages) {
        Write-Host "    pack $($package.Id)"
        & dotnet pack (Join-Path $OrkeonSrc $package.Project) -c Release "-p:Version=$orkeonVersion" -p:NuGetAudit=false -o $feed --nologo -v quiet
        if ($LASTEXITCODE -ne 0) {
            Write-Problem "install-from-source: packing $($package.Id) failed (its message is above). Nothing was installed."
            exit 1
        }
        # A project that is not packable packs nothing and still succeeds.
        if (-not (Test-Path -LiteralPath (Join-Path $feed "$($package.Id).$orkeonVersion.nupkg"))) {
            Write-Problem "install-from-source: packing $($package.Id) wrote no $($package.Id).$orkeonVersion.nupkg into $feed."
            exit 1
        }
    }
    if ($orkeonHead) { [IO.File]::WriteAllText($feedMark, "$orkeonHead`n") }
}

# --- Studio ---------------------------------------------------------------------------------
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

Write-Host ''
Write-Host "==> Publishing Studio for $rid (self-contained: $selfContainedArg)"
foreach ($app in $apps) {
    Write-Host "    publish $($app.Name)"
    & dotnet publish (Join-Path $RepoRoot $app.Project) -c Release -r $rid --self-contained $selfContainedArg `
        "-p:Version=$studioVersion" @versionProps @publishProps -o (Join-Path $stage $app.Name) --nologo -v quiet
    if ($LASTEXITCODE -ne 0) {
        Write-Problem "install-from-source: publishing $($app.Name) failed (its message is above). Nothing was installed."
        exit 1
    }
}

Write-Host ''
Write-Host "==> Installing to $InstallDir"
Assert-StudioNotRunning
New-Item -ItemType Directory -Force -Path $binDir, $libexecDir | Out-Null
foreach ($app in $apps) {
    Remove-StudioApp $app.Name
    Copy-Item -LiteralPath (Join-Path $stage $app.Name) -Destination (Join-Path $libexecDir $app.Name) -Recurse
    # The same launcher the Orkeon archives carry (scripts\installer-assets\wrapper.cmd.tmpl
    # there). cmd.exe wants CRLF.
    $wrapper = @(
        '@echo off',
        "rem Orkeon Studio launcher for $($app.Name) -- $launcherMark.",
        'setlocal',
        'set "ROOT=%~dp0.."',
        'if not defined ORKEON_ESBUILD_PATH if exist "%ROOT%\libexec\esbuild-bin\esbuild.exe" set "ORKEON_ESBUILD_PATH=%ROOT%\libexec\esbuild-bin\esbuild.exe"',
        "`"%ROOT%\libexec\$($app.Name)\$($app.AppHost).exe`" %*",
        'endlocal & exit /b %errorlevel%'
    ) -join "`r`n"
    [IO.File]::WriteAllText((Join-Path $binDir "$($app.Name).cmd"), $wrapper + "`r`n", [Text.Encoding]::ASCII)
}
[IO.File]::WriteAllText((Join-Path $InstallDir 'STUDIO-VERSION'), "$studioVersion`n")

# The WPF app is a window, not a command: a Start menu entry opens it without a console.
$wpfExe = Join-Path $libexecDir 'orkeon-studio\Orkeon.Studio.exe'
try {
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($shortcut)
    $link.TargetPath = $wpfExe
    $link.WorkingDirectory = Split-Path -Parent $wpfExe
    $link.Description = 'Orkeon Studio'
    $link.Save()
} catch {
    Write-Host "Note: the Start menu shortcut could not be written ($($_.Exception.Message)); start $wpfExe directly."
}
Remove-Item -LiteralPath $stage -Recurse -Force

Write-Host ''
Write-Host "Orkeon Studio $studioVersion is installed in $InstallDir, next to the Orkeon CLI."
Write-Host "Start menu: Orkeon Studio. Commands in ${binDir}: $(($apps | ForEach-Object { $_.Name }) -join ', ')"
Write-Host "After reinstalling the CLI, or a 'git pull' here, run this script again; before uninstalling"
Write-Host 'the CLI, run it with -Uninstall.'
