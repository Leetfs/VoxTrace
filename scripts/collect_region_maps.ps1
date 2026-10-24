param(
    [string]$Region = "us",
    [int]$SecondsPerMap = 90,
    [string]$OutDir = "captures"
)

$ErrorActionPreference = "Stop"

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "This script must run in an elevated PowerShell (Run as Administrator)." -ForegroundColor Red
    exit 1
}

$root = Split-Path -Parent $PSScriptRoot
if (-not [System.IO.Path]::IsPathRooted($OutDir)) {
    $OutDir = Join-Path $root $OutDir
}
if (-not (Test-Path $OutDir)) {
    New-Item -ItemType Directory -Force $OutDir | Out-Null
}

$results = @()

Write-Host ""
Write-Host "Region: $Region    seconds per map: $SecondsPerMap" -ForegroundColor Cyan
Write-Host "Enter a map name, switch to it during the countdown, repeat for each map." -ForegroundColor Yellow
Write-Host "Leave the name empty to finish and aggregate." -ForegroundColor Yellow

while ($true) {
    $map = Read-Host "`nMap name (empty = finish)"
    if ([string]::IsNullOrWhiteSpace($map)) { break }

    $safe = $map -replace '[^\w\-\.]', '_'
    $label = "$Region-$safe"

    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "find_region_ips.ps1") `
        -Region $label -Seconds $SecondsPerMap -OutDir $OutDir

    if ($LASTEXITCODE -ne 0) {
        Write-Host "Capture failed for $map" -ForegroundColor Red
        continue
    }

    $latest = Get-ChildItem $OutDir -Directory -Filter ("region_{0}_*" -f $label) |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $latest) { continue }

    $cidrFile = Join-Path $latest.FullName "cidrs.txt"
    $cidrs = @()
    if (Test-Path $cidrFile) {
        $cidrs = @(Get-Content $cidrFile -Encoding UTF8 | Where-Object { $_.Trim() })
    }

    $results += [pscustomobject]@{
        Map    = $map
        Folder = $latest.FullName
        Cidrs  = ($cidrs -join ",")
    }
    Write-Host ("  -> {0} CIDR(s)" -f $cidrs.Count) -ForegroundColor Green
}

if ($results.Count -eq 0) {
    Write-Host "No captures." -ForegroundColor Yellow
    exit 0
}

$csv = Join-Path $OutDir ("region_{0}_maps.csv" -f $Region)
$results | Export-Csv -Path $csv -NoTypeInformation -Encoding UTF8

$counts = @{}
foreach ($r in $results) {
    foreach ($c in ($r.Cidrs -split ",")) {
        if ([string]::IsNullOrWhiteSpace($c)) { continue }
        if ($counts.ContainsKey($c)) { $counts[$c]++ } else { $counts[$c] = 1 }
    }
}

$agg = $counts.GetEnumerator() |
    Sort-Object -Property @{ Expression = "Value"; Descending = $true }, @{ Expression = "Name"; Descending = $false }
$aggCsv = Join-Path $OutDir ("region_{0}_cidr_counts.csv" -f $Region)
$agg | Select-Object @{ n = "Cidr"; e = { $_.Name } }, @{ n = "Maps"; e = { $_.Value } } |
    Export-Csv -Path $aggCsv -NoTypeInformation -Encoding UTF8

Write-Host ""
Write-Host "Per-map:" -ForegroundColor Cyan
$results | Format-Table Map, Cidrs -AutoSize

Write-Host "CIDR frequency:" -ForegroundColor Cyan
$agg | Select-Object @{ n = "Cidr"; e = { $_.Name } }, @{ n = "Maps"; e = { $_.Value } } |
    Format-Table -AutoSize

Write-Host ("Files: {0}" -f $csv) -ForegroundColor Green
Write-Host ("Files: {0}" -f $aggCsv) -ForegroundColor Green
