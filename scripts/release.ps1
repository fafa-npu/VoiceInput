<#
.SYNOPSIS
  Build a versioned self-contained exe and publish it as a GitHub Enterprise release.

.DESCRIPTION
  Older `gh` builds mishandle this GHE instance's release asset upload, so this script creates the
  release and uploads the exe via the REST API directly (using the token from `gh auth`).
  Prerequisite: gh auth login --hostname microsoft.ghe.com

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\release.ps1 -Version v0.1.1
#>
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$Version)   # e.g. v0.1.1

$ErrorActionPreference = 'Stop'
$Repo    = 'Zhao-Hua/VoiceInput'
$GheHost = 'microsoft.ghe.com'
$Api     = "https://$GheHost/api/v3"
$Uploads = "https://uploads.$GheHost"

$repoRoot = Split-Path (Split-Path $PSCommandPath -Parent) -Parent
$proj   = Join-Path $repoRoot 'src\VoiceInput\VoiceInput.csproj'
$pubDir = Join-Path $repoRoot 'publish'
$exe    = Join-Path $pubDir 'VoiceInput.exe'
$num    = $Version.TrimStart('v', 'V')

$token = gh auth token --hostname $GheHost
if (-not $token) { throw "No GHE token. Run once: gh auth login --hostname $GheHost" }
$H = @{ Authorization = "token $token"; Accept = 'application/vnd.github+json' }

# Build the self-contained single-file exe with the release version baked in.
Write-Host "Building $Version..." -ForegroundColor Cyan
$pubArgs = @(
    'publish', $proj, '-c', 'Release', '-r', 'win-x64', "-p:Version=$num",
    '-p:SelfContained=true', '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true',
    '-o', $pubDir
)
& dotnet @pubArgs | Out-Null
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exe)) { throw 'dotnet publish failed.' }

# Create the (published) release, or reuse it if the tag already exists.
$body = @{
    tag_name = $Version
    name     = "VoiceInput $Version"
    draft    = $false
    body     = "Self-contained Windows build - no .NET install needed. Download VoiceInput.exe and double-click to run, or for auto-start at login: scripts/install.ps1 -Source VoiceInput.exe"
} | ConvertTo-Json
try {
    $rel = Invoke-RestMethod -Uri "$Api/repos/$Repo/releases" -Method Post -Headers $H -ContentType 'application/json' -Body $body
}
catch {
    $rel = Invoke-RestMethod -Uri "$Api/repos/$Repo/releases/tags/$Version" -Headers $H
    if ($rel.draft) {
        $rel = Invoke-RestMethod -Uri "$Api/repos/$Repo/releases/$($rel.id)" -Method Patch -Headers $H -ContentType 'application/json' -Body '{"draft":false}'
    }
}

# Replace any existing asset of the same name, then upload the exe via REST
# (gh's own asset upload is broken on this GHE instance).
foreach ($a in @($rel.assets | Where-Object { $_.name -eq 'VoiceInput.exe' })) {
    try { Invoke-RestMethod -Uri "$Api/repos/$Repo/releases/assets/$($a.id)" -Method Delete -Headers $H | Out-Null } catch {}
}
Write-Host "Uploading VoiceInput.exe..." -ForegroundColor Cyan
$up = "$Uploads/repos/$Repo/releases/$($rel.id)/assets?name=VoiceInput.exe"
try {
    $r = Invoke-WebRequest -Uri $up -Method Post -Headers $H -ContentType 'application/octet-stream' -InFile $exe -TimeoutSec 600
    if ($r.StatusCode -ne 201) { throw "HTTP $($r.StatusCode)" }
}
catch {
    throw "Asset upload failed: $($_.Exception.Message)"
}

Write-Host "Released $Version" -ForegroundColor Green
Write-Host "  Page:     $($rel.html_url)"
Write-Host "  Download: https://$GheHost/$Repo/releases/download/$Version/VoiceInput.exe"
