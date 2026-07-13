<#
.SYNOPSIS
  Build a versioned self-contained exe and publish it as a GitHub Enterprise release.

.DESCRIPTION
  Older `gh` builds mishandle release asset upload on some hosts, so this script creates the
  release and uploads the exe via the REST API directly (using the token from `gh auth`).
  Prerequisite: gh auth login --hostname github.com

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\release.ps1 -Version v0.1.1
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$SignPfx
)

$ErrorActionPreference = 'Stop'
$Repo    = 'fafa-npu/VoiceInput'
$Site    = 'github.com'
$Api     = 'https://api.github.com'
$Uploads = 'https://uploads.github.com'

$repoRoot = Split-Path (Split-Path $PSCommandPath -Parent) -Parent
$proj   = Join-Path $repoRoot 'src\VoiceInput\VoiceInput.csproj'
$pubDir = Join-Path $repoRoot 'publish'
$exe    = Join-Path $pubDir 'VoiceInput.exe'
$num    = $Version.TrimStart('v', 'V')
if (-not (Test-Path -LiteralPath $SignPfx)) { throw "Signing certificate not found: $SignPfx" }
$signPassword = Read-Host -Prompt 'PFX password' -AsSecureString
$certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $SignPfx, $signPassword,
    [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
$signerSha256 = $certificate.GetCertHashString(
    [System.Security.Cryptography.HashAlgorithmName]::SHA256)

$token = gh auth token --hostname $Site
if (-not $token) { throw "No token. Run once: gh auth login --hostname $Site" }
$H = @{ Authorization = "token $token"; Accept = 'application/vnd.github+json' }

# Build the self-contained single-file exe with the release version baked in.
Write-Host "Building $Version..." -ForegroundColor Cyan
$pubArgs = @(
    'publish', $proj, '-c', 'Release', '-r', 'win-x64', "-p:Version=$num",
    "-p:UpdateSignerCertificateSha256=$signerSha256",
    '-p:SelfContained=true', '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true',
    '-o', $pubDir
)
& dotnet @pubArgs | Out-Null
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exe)) { throw 'dotnet publish failed.' }
$signature = Set-AuthenticodeSignature -FilePath $exe -Certificate $certificate -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com'
if ($signature.Status -ne 'Valid') { throw "Authenticode signing failed: $($signature.StatusMessage)" }

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
# The upload succeeds (asset is created), but Invoke-WebRequest's response object is unreliable here
# and reading $r.StatusCode can throw a spurious null-ref. So don't trust the call's return value —
# verify success by querying the release's assets and confirming size + uploaded state.
try { Invoke-WebRequest -Uri $up -Method Post -Headers $H -ContentType 'application/octet-stream' -InFile $exe -TimeoutSec 600 | Out-Null }
catch { Write-Host "  (upload call returned an error; verifying via API…)" -ForegroundColor DarkYellow }

$expected = (Get-Item $exe).Length
$asset = $null
foreach ($attempt in 1..5) {
    Start-Sleep -Seconds 2
    $check = Invoke-RestMethod -Uri "$Api/repos/$Repo/releases/$($rel.id)" -Headers $H
    $asset = $check.assets | Where-Object { $_.name -eq 'VoiceInput.exe' } | Select-Object -First 1
    if ($asset -and $asset.state -eq 'uploaded' -and $asset.size -eq $expected) { break }
    $asset = $null
}
if (-not $asset) { throw "Asset upload could not be verified (no uploaded VoiceInput.exe of $expected bytes on $Version)." }

Write-Host "Released $Version" -ForegroundColor Green
Write-Host "  Page:     $($rel.html_url)"
Write-Host "  Download: https://$Site/$Repo/releases/download/$Version/VoiceInput.exe"
