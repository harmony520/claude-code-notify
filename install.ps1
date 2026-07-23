<#
.SYNOPSIS
    Build claude-notify.exe and register it as Claude Code Notification/Stop hooks.

.DESCRIPTION
    1. Locates the C# compiler (csc.exe) shipped with the .NET Framework — no SDK
       or external toolchain required; it's present on every Windows 10/11.
    2. Compiles claude-notify.cs into claude-notify.exe next to this script.
    3. Merges the two hooks (Notification -> amber "waiting", Stop -> green "done")
       into your Claude Code settings.json WITHOUT clobbering anything else in it.

.PARAMETER SettingsPath
    Path to the Claude Code settings.json to update. Defaults to the per-user file
    at %USERPROFILE%\.claude\settings.json.

.PARAMETER Uninstall
    Remove the two hooks this installer added and leave the rest of settings.json
    untouched. Does not delete the compiled exe.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File install.ps1
#>
[CmdletBinding()]
param(
    [string]$SettingsPath = (Join-Path $env:USERPROFILE ".claude\settings.json"),
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
$here    = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcPath = Join-Path $here "claude-notify.cs"
$exePath = Join-Path $here "claude-notify.exe"

function Write-Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Write-Ok($m)   { Write-Host "    $m" -ForegroundColor Green }
function Write-Warn($m) { Write-Host "    $m" -ForegroundColor Yellow }

# --------------------------------------------------------------------------
# Uninstall: strip the hooks whose command points at our exe, then exit.
# --------------------------------------------------------------------------
if ($Uninstall) {
    if (-not (Test-Path $SettingsPath)) { Write-Warn "settings.json not found; nothing to remove."; return }
    $json = Get-Content -Raw -Path $SettingsPath | ConvertFrom-Json
    $removed = 0
    foreach ($event in @("Notification","Stop")) {
        if ($json.hooks -and $json.hooks.$event) {
            $kept = @()
            foreach ($entry in @($json.hooks.$event)) {
                $isOurs = $false
                foreach ($h in @($entry.hooks)) {
                    if ($h.command -and $h.command -match "claude-notify\.exe") { $isOurs = $true }
                }
                if ($isOurs) { $removed++ } else { $kept += $entry }
            }
            if ($kept.Count -gt 0) { $json.hooks.$event = $kept }
            else { $json.hooks.PSObject.Properties.Remove($event) }
        }
    }
    $json | ConvertTo-Json -Depth 20 | Set-Content -Path $SettingsPath -Encoding UTF8
    Write-Ok "Removed $removed hook(s) from $SettingsPath"
    return
}

# --------------------------------------------------------------------------
# 1. Locate csc.exe (.NET Framework compiler — always on Windows).
# --------------------------------------------------------------------------
Write-Step "Locating C# compiler (csc.exe)"
$cscCandidates = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
$csc = $cscCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) {
    throw "csc.exe not found. This needs the .NET Framework 4.x compiler, which ships with Windows 10/11. Looked in: $($cscCandidates -join '; ')"
}
Write-Ok "Using $csc"

# --------------------------------------------------------------------------
# 2. Compile.
# --------------------------------------------------------------------------
Write-Step "Compiling claude-notify.exe"
if (-not (Test-Path $srcPath)) { throw "Source not found: $srcPath" }
# Kill any stale copy still holding the exe open, so the build can overwrite it.
Get-Process -Name "claude-notify" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

$cscArgs = @(
    "/nologo", "/optimize+", "/target:winexe", "/codepage:65001",
    "/r:System.dll", "/r:System.Drawing.dll", "/r:System.Windows.Forms.dll",
    "/r:System.Management.dll",
    "/out:$exePath", $srcPath
)
& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "Compilation failed (csc exit $LASTEXITCODE)." }
if (-not (Test-Path $exePath)) { throw "Compilation reported success but $exePath is missing." }
Write-Ok "Built $exePath"

# --------------------------------------------------------------------------
# 3. Merge hooks into settings.json (create it if missing).
# --------------------------------------------------------------------------
Write-Step "Registering hooks in $SettingsPath"
$settingsDir = Split-Path -Parent $SettingsPath
if (-not (Test-Path $settingsDir)) { New-Item -ItemType Directory -Force -Path $settingsDir | Out-Null }

if (Test-Path $SettingsPath) {
    # Back up before touching an existing config.
    $backup = "$SettingsPath.bak"
    Copy-Item -Path $SettingsPath -Destination $backup -Force
    Write-Ok "Backed up existing settings to $backup"
    $json = Get-Content -Raw -Path $SettingsPath | ConvertFrom-Json
} else {
    $json = [PSCustomObject]@{}
}

# Forward slashes work fine on Windows and avoid JSON backslash-escaping noise.
$exeFwd  = ($exePath -replace '\\','/')
$cmdConfirm = "`"$exeFwd`" --scenario confirm"
$cmdDone    = "`"$exeFwd`" --scenario done"

function New-HookEntry($command) {
    [PSCustomObject]@{
        matcher = ""
        hooks   = @( [PSCustomObject]@{ type = "command"; command = $command } )
    }
}

# Ensure a .hooks object exists.
if (-not $json.PSObject.Properties.Match("hooks").Count) {
    $json | Add-Member -MemberType NoteProperty -Name hooks -Value ([PSCustomObject]@{})
}

# For each event: drop any prior claude-notify entry (idempotent re-install),
# keep everything else, then append ours.
foreach ($pair in @(@("Notification",$cmdConfirm), @("Stop",$cmdDone))) {
    $event = $pair[0]; $cmd = $pair[1]
    $existing = @()
    if ($json.hooks.PSObject.Properties.Match($event).Count) {
        foreach ($entry in @($json.hooks.$event)) {
            $isOurs = $false
            foreach ($h in @($entry.hooks)) {
                if ($h.command -and $h.command -match "claude-notify\.exe") { $isOurs = $true }
            }
            if (-not $isOurs) { $existing += $entry }
        }
    }
    $existing += New-HookEntry $cmd
    if ($json.hooks.PSObject.Properties.Match($event).Count) { $json.hooks.$event = $existing }
    else { $json.hooks | Add-Member -MemberType NoteProperty -Name $event -Value $existing }
}

$json | ConvertTo-Json -Depth 20 | Set-Content -Path $SettingsPath -Encoding UTF8
Write-Ok "Hooks registered."

Write-Host ""
Write-Host "Done. Restart Claude Code (or start a new session) so it picks up the hooks." -ForegroundColor Green
Write-Host "Test now with:" -ForegroundColor Green
Write-Host "    `"$exePath`" --scenario done" -ForegroundColor Gray
