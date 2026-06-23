param(
    [Parameter(Mandatory = $true)][string]$Source
)

# Installs the published VoiceInput.exe to the per-user app folder and adds a Startup shortcut.
$ErrorActionPreference = 'Stop'

$dest = Join-Path $env:LOCALAPPDATA 'VoiceInput'
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item -Path $Source -Destination (Join-Path $dest 'VoiceInput.exe') -Force
Write-Host "Installed to $dest\VoiceInput.exe"

$startup = [Environment]::GetFolderPath('Startup')
$shortcut = Join-Path $startup 'VoiceInput.lnk'
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($shortcut)
$lnk.TargetPath = (Join-Path $dest 'VoiceInput.exe')
$lnk.WorkingDirectory = $dest
$lnk.Description = 'VoiceInput - hold-to-talk voice input'
$lnk.Save()
Write-Host "Startup shortcut created at $shortcut"
