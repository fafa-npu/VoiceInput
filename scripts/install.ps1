<#
.SYNOPSIS
  One-click installer for VoiceInput.

.DESCRIPTION
  Installs VoiceInput to %LOCALAPPDATA%\VoiceInput, enables auto-start at login, and launches it.
  With no -Source, it builds a self-contained single-file exe from source (needs the .NET 10 SDK).
  With -Source <exe>, it installs a prebuilt exe (e.g. one downloaded from a Release) - no SDK needed.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\install.ps1

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Source .\VoiceInput.exe

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string]$Source,       # prebuilt exe to install; if omitted, builds from source
    [switch]$Uninstall,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$AppName    = 'VoiceInput'
$ExeName    = 'VoiceInput.exe'
$InstallDir = Join-Path $env:LOCALAPPDATA $AppName
$StartupLnk = Join-Path ([Environment]::GetFolderPath('Startup')) ($AppName + '.lnk')

function Stop-App {
    Get-Process $AppName -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 400
}

if ($Uninstall) {
    Stop-App
    if (Test-Path $StartupLnk) { Remove-Item $StartupLnk -Force }
    if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
    Write-Host 'VoiceInput uninstalled.' -ForegroundColor Green
    return
}

$repoRoot = Split-Path (Split-Path $PSCommandPath -Parent) -Parent

# Resolve the exe to install - build from source when no prebuilt -Source was given.
if (-not $Source) {
    $proj = Join-Path $repoRoot 'src\VoiceInput\VoiceInput.csproj'
    if (-not (Test-Path $proj)) {
        throw "No -Source exe given and project not found at $proj. Run from the repo, or pass -Source <exe>."
    }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'No -Source exe given and the .NET SDK (dotnet) is not installed. Install the .NET 10 SDK, or pass -Source <exe>.'
    }
    Write-Host 'Building self-contained release (this can take a minute)...' -ForegroundColor Cyan
    $pubDir = Join-Path $repoRoot 'publish'
    $pubArgs = @(
        'publish', $proj, '-c', 'Release', '-r', 'win-x64',
        '-p:SelfContained=true', '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true',
        '-o', $pubDir
    )
    & dotnet @pubArgs | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
    $Source = Join-Path $pubDir $ExeName
}

if (-not (Test-Path $Source)) { throw "Installer source not found: $Source" }

Stop-App
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item $Source (Join-Path $InstallDir $ExeName) -Force
Write-Host "Installed to $InstallDir\$ExeName" -ForegroundColor Green

# Auto-start at login via a Startup-folder shortcut.
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($StartupLnk)
$lnk.TargetPath       = Join-Path $InstallDir $ExeName
$lnk.WorkingDirectory = $InstallDir
$lnk.Description       = 'VoiceInput - hold-to-talk voice input'
$lnk.Save()
Write-Host 'Auto-start enabled (runs at login).' -ForegroundColor Green

if (-not $NoLaunch) {
    Start-Process (Join-Path $InstallDir $ExeName)
    Write-Host 'VoiceInput is running - hold Right Ctrl to talk. Tray icon (blue mic) is bottom-right.' -ForegroundColor Green
}
Write-Host 'Uninstall any time: scripts\install.ps1 -Uninstall'
