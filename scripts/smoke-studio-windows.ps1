#Requires -Version 7.0
<#
.SYNOPSIS
  Smokes an installed Orkeon Studio tree on Windows: the payload is there and the WPF
  app actually starts.
.DESCRIPTION
  Adapted from Orkeon's scripts/smoke-onboarding/lib/studio-windows.ps1. Checks
  bin\orkeon-studio.cmd and libexec\orkeon-studio\Orkeon.Studio.exe, optionally the
  "Orkeon Studio" Start-menu shortcut the MSI adds, then runs
  `orkeon-studio.cmd --smoke-exit`: the app opens its window, waits for the first
  render and exits 0. That turns "the file is there" into "the app runs": a WPF binary
  can be present and still fail to resolve its XAML, its runtime or its ViewModels.
  The launch goes through the .cmd launcher, so the launcher is exercised too.
.PARAMETER InstallRoot
  The folder holding bin\ and libexec\ (an extracted zip, or the MSI install folder).
.PARAMETER LogDir
  Where the --smoke-exit stdout and stderr captures go.
.PARAMETER TimeoutSeconds
  How long the window may take to open and close again.
.PARAMETER ExpectStartMenuShortcut
  Also require the Start-menu shortcut (the MSI channel only).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$InstallRoot,
    [Parameter(Mandatory = $true)][string]$LogDir,
    [int]$TimeoutSeconds = 120,
    [switch]$ExpectStartMenuShortcut
)
$ErrorActionPreference = 'Stop'

$problems = New-Object System.Collections.ArrayList
$studioCmd = Join-Path $InstallRoot 'bin\orkeon-studio.cmd'
$studioExe = Join-Path $InstallRoot 'libexec\orkeon-studio\Orkeon.Studio.exe'

foreach ($path in @($studioCmd, $studioExe)) {
    if (-not (Test-Path -LiteralPath $path)) { [void]$problems.Add("missing: $path") }
}

if ($ExpectStartMenuShortcut) {
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Orkeon Studio.lnk'
    if (Test-Path -LiteralPath $shortcut) {
        Write-Host "PASS shortcut -- $shortcut"
    } else {
        [void]$problems.Add("no Start-menu shortcut at $shortcut")
    }
}

if ($problems.Count -eq 0) {
    New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
    $outFile = Join-Path $LogDir 'studio-smoke-exit.out.txt'
    $errFile = Join-Path $LogDir 'studio-smoke-exit.err.txt'

    # A background job purely for the timeout: a GUI app that hangs on startup would
    # otherwise eat the job's budget and surface as an unattributed workflow timeout.
    $job = Start-Job -ScriptBlock {
        param($Cmd, $Out, $Err)
        $ErrorActionPreference = 'Continue'
        & $Cmd '--smoke-exit' 1> $Out 2> $Err
        $LASTEXITCODE
    } -ArgumentList $studioCmd, $outFile, $errFile

    if (-not (Wait-Job -Job $job -Timeout $TimeoutSeconds)) {
        Stop-Job -Job $job -ErrorAction SilentlyContinue
        [void]$problems.Add("orkeon-studio --smoke-exit did not exit within $TimeoutSeconds s (window never closed?)")
    } else {
        $exitCode = @(Receive-Job -Job $job -ErrorAction SilentlyContinue) | Select-Object -Last 1
        if ($null -eq $exitCode -or $exitCode -ne 0) {
            $tail = ''
            if (Test-Path -LiteralPath $errFile) {
                $tail = (Get-Content -LiteralPath $errFile -Tail 5 -ErrorAction SilentlyContinue) -join ' / '
            }
            # A null exit code means the launcher never ran (the job died before it).
            $message = if ($null -eq $exitCode) { 'orkeon-studio --smoke-exit produced no exit code (launcher never ran?)' }
                       else { "orkeon-studio --smoke-exit exited $exitCode (expected 0)" }
            if ($tail) { $message = "$message -- $tail" }
            [void]$problems.Add($message)
        } else {
            Write-Host 'PASS smoke-exit -- orkeon-studio opened and closed its window, exit 0'
        }
    }
    Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
}

if ($problems.Count -gt 0) {
    foreach ($problem in $problems) { Write-Host "FAIL $problem" }
    exit 1
}
exit 0
