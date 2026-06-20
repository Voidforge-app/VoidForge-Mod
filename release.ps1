# Bumps the semver version across all relevant files, builds, and publishes a GitHub release.
# Requires: GitHub CLI (gh) installed and authenticated.
#
# Usage:
#   .\release.ps1 -Bump patch        # 1.0.0 -> 1.0.1
#   .\release.ps1 -Bump minor        # 1.0.0 -> 1.1.0
#   .\release.ps1 -Bump major        # 1.0.0 -> 2.0.0
#   .\release.ps1 -Version 1.2.3     # set explicit version
#   .\release.ps1 -Bump patch -Message "Adds X and fixes Y"
[CmdletBinding(DefaultParameterSetName = "Bump")]
param(
    [Parameter(ParameterSetName = "Bump", Mandatory)]
    [ValidateSet("patch", "minor", "major")]
    [string]$Bump,

    [Parameter(ParameterSetName = "Explicit", Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [Parameter()]
    [string]$Message
)

$ErrorActionPreference = "Stop"

# --- Preflight --- 
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Error "GitHub CLI (gh) is not installed. Install from https://cli.github.com"
    exit 1
}

$gitStatus = git status --porcelain
if ($gitStatus) {
    Write-Error "Working tree is not clean. Commit or stash changes before releasing."
    exit 1
}

# --- Resolve new version ---
$csprojPath = "BlueprintExtractor\BlueprintExtractor.csproj"
$csprojContent = Get-Content $csprojPath -Raw
$currentVersion = [regex]::Match($csprojContent, '<Version>(\d+\.\d+\.\d+)</Version>').Groups[1].Value

if (-not $currentVersion) {
    Write-Error "Could not read current version from $csprojPath"
    exit 1
}

if ($PSCmdlet.ParameterSetName -eq "Bump") {
    $parts = $currentVersion.Split(".")
    $majorPart = [int]$parts[0]
    $minorPart = [int]$parts[1]
    $patchPart = [int]$parts[2]
    switch ($Bump) {
        "major" { $majorPart++; $minorPart = 0; $patchPart = 0 }
        "minor" { $minorPart++; $patchPart = 0 }
        "patch" { $patchPart++ }
    }
    $Version = "$majorPart.$minorPart.$patchPart"
}

$tag     = "v$Version"
$zipPath = "BlueprintExtractor\bin\BlueprintExtractor-$Version.zip"

Write-Host "  $currentVersion  ->  $Version" -ForegroundColor Yellow

# --- Generate changelog before the version-bump commit ---
$ErrorActionPreference = "SilentlyContinue"
$previousTag = git describe --tags --abbrev=0 2>&1 | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] }
$hadPreviousTag = $LASTEXITCODE -eq 0 -and $previousTag
$ErrorActionPreference = "Stop"
$commitRange = if ($hadPreviousTag) { "$previousTag..HEAD" } else { "HEAD" }
$changelogLines = git log $commitRange --pretty=format:"- %s (%h)" --no-merges
$changelog = $changelogLines -join "`n"
if (-not $changelog) { $changelog = "- Initial release" }

$releaseNotes = if ($Message) { "$Message`n`n## Changelog`n$changelog" } else { $changelog }

# --- Bump version in all relevant files ---
Write-Host "==> Updating version files ..." -ForegroundColor Cyan

# BlueprintExtractor.csproj
$csprojContent = $csprojContent -replace '<Version>\d+\.\d+\.\d+</Version>', "<Version>$Version</Version>"
Set-Content $csprojPath $csprojContent -NoNewline

# Info.json (UMM mod metadata bundled inside the zip)
$infoPath = "BlueprintExtractor\Info.json"
$infoJson = Get-Content $infoPath -Raw | ConvertFrom-Json
$infoJson.Version = $Version
$infoJson | ConvertTo-Json -Depth 5 | Set-Content $infoPath

# Repository.json (UMM repository manifest for auto-update)
$repoPath = "Repository.json"
$repoJson = Get-Content $repoPath -Raw | ConvertFrom-Json
$repoJson.Releases[0].Version = $Version
$repoJson | ConvertTo-Json -Depth 5 | Set-Content $repoPath

git add $csprojPath $infoPath $repoPath
git commit -m "chore: bump version to $Version"
if ($LASTEXITCODE -ne 0) { Write-Error "git commit failed."; exit 1 }

# --- Build ---
Write-Host "==> Building BlueprintExtractor $Version ..." -ForegroundColor Cyan
dotnet build VoidForge.slnx -c Release --nologo
if ($LASTEXITCODE -ne 0) { Write-Error "Build failed."; exit 1 }

if (-not (Test-Path $zipPath)) {
    Write-Error "Expected release zip not found at $zipPath"
    exit 1
}

# --- Tag and push ---
Write-Host "==> Tagging $tag ..." -ForegroundColor Cyan
git tag $tag
if ($LASTEXITCODE -ne 0) { Write-Error "git tag failed -- tag may already exist."; exit 1 }

git push
if ($LASTEXITCODE -ne 0) { Write-Error "git push failed."; exit 1 }

git push origin $tag
if ($LASTEXITCODE -ne 0) { Write-Error "git push tag failed."; exit 1 }

# --- Publish release ---
Write-Host "==> Creating GitHub release $tag ..." -ForegroundColor Cyan
gh release create $tag $zipPath `
    --title "BlueprintExtractor $Version" `
    --notes $releaseNotes
if ($LASTEXITCODE -ne 0) { Write-Error "gh release create failed."; exit 1 }

Write-Host "==> Released $tag" -ForegroundColor Green
