# Upload JSON exports to the voidforge-dev R2 bucket, optionally updating latest.json.

$exportRoot = Join-Path ([Environment]::GetFolderPath('MyDocuments')) "VoidForge"
$remote     = "VoidforgeR2"
$bucket     = "voidforge-dev"

# --- Version selection ---
$versionDirs = Get-ChildItem -Path $exportRoot -Directory |
    Where-Object { $_.Name -match '^\d+\.\d+' } |
    Sort-Object { try { [version]$_.Name } catch { [version]"0.0.0.0" } } -Descending

if ($versionDirs.Count -eq 0) {
    Write-Host "No version directories found in $exportRoot" -ForegroundColor Red
    Read-Host "Press Enter to exit"; exit
}

$newestVersion = $versionDirs[0].Name

Write-Host "`nAvailable versions:" -ForegroundColor Cyan
for ($i = 0; $i -lt $versionDirs.Count; $i++) {
    $marker = if ($i -eq 0) { "  <-- default (newest)" } else { "" }
    Write-Host "  [$($i + 1)] $($versionDirs[$i].Name)$marker"
}

$versionInput = Read-Host "`nSelect version [1]"
if ([string]::IsNullOrWhiteSpace($versionInput)) { $versionInput = "1" }
$versionIndex = [int]$versionInput - 1

if ($versionIndex -lt 0 -or $versionIndex -ge $versionDirs.Count) {
    Write-Host "Invalid selection" -ForegroundColor Red
    Read-Host "Press Enter to exit"; exit
}

$selectedVersion = $versionDirs[$versionIndex].Name
$sourcePath      = Join-Path $exportRoot $selectedVersion
$isNewest        = $selectedVersion -eq $newestVersion

$destination = "${remote}:${bucket}/${selectedVersion}"
 
# --- Confirm ---
Write-Host "`nUpload summary:" -ForegroundColor Yellow
Write-Host "  Source      : $sourcePath  (excluding *.log)"
Write-Host "  Destination : $destination"
Write-Host "  Update latest.json : $(if ($isNewest) { 'yes' } else { 'no (not newest)' })"

$confirm = Read-Host "`nProceed? [Y/n]"
if ($confirm -match '^[Nn]') {
    Write-Host "Cancelled." -ForegroundColor Yellow
    Read-Host "Press Enter to exit"; exit
}

# --- Upload JSON files ---
Write-Host "`nUploading JSON files..." -ForegroundColor Cyan
& rclone sync $sourcePath $destination `
    --progress `
    --transfers 32 `
    --checkers 16 `
    --buffer-size 16M `
    --exclude "*.log"

if ($LASTEXITCODE -ne 0) {
    Write-Host "`nrclone exited with code $LASTEXITCODE -- aborting." -ForegroundColor Red
    Read-Host "Press Enter to exit"; exit
}

Write-Host "`nUpload complete." -ForegroundColor Green

# --- Update latest.json if this is the newest version ---
if ($isNewest) {
    Write-Host "`nUpdating latest.json..." -ForegroundColor Cyan

    $latestJson  = @{ version = $selectedVersion } | ConvertTo-Json -Compress
    $tempLatest  = Join-Path $env:TEMP "voidforge-latest.json"
    $latestJson | Set-Content -Path $tempLatest -Encoding UTF8 -NoNewline

    & rclone copyto $tempLatest "${remote}:${bucket}/latest.json" --progress

    Remove-Item $tempLatest -Force

    if ($LASTEXITCODE -eq 0) {
        Write-Host "latest.json updated to $selectedVersion" -ForegroundColor Green
    } else {
        Write-Host "Failed to update latest.json (exit $LASTEXITCODE)" -ForegroundColor Red
    }
} else {
    Write-Host "`nSkipping latest.json -- $selectedVersion is not the newest ($newestVersion)." -ForegroundColor Yellow
}

Write-Host ""
Read-Host "Press Enter to exit"
