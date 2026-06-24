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
$InstallDir   = Join-Path $env:LOCALAPPDATA $AppName
$StartupLnk   = Join-Path ([Environment]::GetFolderPath('Startup')) ($AppName + '.lnk')
$StartMenuLnk = Join-Path ([Environment]::GetFolderPath('Programs')) ($AppName + '.lnk')

function New-AppShortcut($path) {
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($path)
    $lnk.TargetPath       = Join-Path $InstallDir $ExeName
    $lnk.WorkingDirectory = $InstallDir
    $lnk.Description       = 'VoiceInput - hold-to-talk voice input'
    $lnk.Save()
}

function Stop-App {
    Get-Process $AppName -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 400
}

if ($Uninstall) {
    Stop-App
    if (Test-Path $StartupLnk) { Remove-Item $StartupLnk -Force }
    if (Test-Path $StartMenuLnk) { Remove-Item $StartMenuLnk -Force }
    if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
    Write-Host 'VoiceInput uninstalled.' -ForegroundColor Green
    return
}

$repoRoot = Split-Path (Split-Path $PSCommandPath -Parent) -Parent

# Resolve the exe: -Source if given; else build from a clone; else download the latest release.
if (-not $Source) {
    $proj = Join-Path $repoRoot 'src\VoiceInput\VoiceInput.csproj'
    if (Test-Path $proj) {
        if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
            throw 'Building from source needs the .NET 10 SDK (dotnet). Install it, or pass -Source <exe>.'
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
    else {
        # Not run from a clone (e.g. the one-line web installer): download the latest release exe.
        Write-Host 'Downloading the latest VoiceInput.exe...' -ForegroundColor Cyan
        $Source = Join-Path $env:TEMP $ExeName
        Invoke-WebRequest 'https://microsoft.ghe.com/Zhao-Hua/VoiceInput/releases/latest/download/VoiceInput.exe' -OutFile $Source
    }
}

if (-not (Test-Path $Source)) { throw "Installer source not found: $Source" }

Stop-App
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item $Source (Join-Path $InstallDir $ExeName) -Force
Write-Host "Installed to $InstallDir\$ExeName" -ForegroundColor Green

# Auto-start at login (Startup folder) + a Start Menu entry so it's launchable by name.
New-AppShortcut $StartupLnk
New-AppShortcut $StartMenuLnk
Write-Host 'Auto-start enabled (runs at login); Start Menu entry created.' -ForegroundColor Green

if (-not $NoLaunch) {
    Start-Process (Join-Path $InstallDir $ExeName)
    Write-Host 'VoiceInput is running - hold Right Ctrl to talk. Tray icon (blue mic) is bottom-right.' -ForegroundColor Green
}
Write-Host 'Uninstall any time: scripts\install.ps1 -Uninstall'
