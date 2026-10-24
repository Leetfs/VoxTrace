param(
    [string]$Region = "us",
    [int]$Seconds = 90,
    [int]$Port = 27002,
    [string]$OutDir = "captures"
)

$ErrorActionPreference = "Stop"

# --- admin check -----------------------------------------------------------
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

$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$work = Join-Path $OutDir ("region_{0}_{1}" -f $Region, $stamp)
New-Item -ItemType Directory -Force $work | Out-Null

Write-Host ""
Write-Host "Region label : $Region"
Write-Host "Port filter  : UDP $Port"
Write-Host "Output       : $work" -ForegroundColor Cyan
Write-Host ""
Write-Host "Before starting: quit Clash / UU TUN if you want plaintext on the physical NIC." -ForegroundColor Yellow
Write-Host "Then, after the countdown, switch VRChat to the target region and enter a world." -ForegroundColor Yellow
Write-Host ""

# --- start capture ---------------------------------------------------------
pktmon filter remove | Out-Null
pktmon filter add VoxTrace -t UDP -p $Port | Out-Null

Push-Location $work
try {
    pktmon start --capture --pkt-size 256 | Out-Null
    Write-Host "Capture started. Switch region now." -ForegroundColor Green

    for ($i = $Seconds; $i -gt 0; $i--) {
        Write-Host ("  capturing... {0,2}s left (Ctrl+C to abort)" -f $i)
        Start-Sleep -Seconds 1
    }

    pktmon stop | Out-Null
    Write-Host "Capture stopped." -ForegroundColor Green

    if (Test-Path "PktMon.etl") {
        pktmon etl2txt "PktMon.etl" -o "pm.txt" | Out-Null
        pktmon etl2pcap "PktMon.etl" -o "photon.pcapng" | Out-Null
    } else {
        Write-Host "PktMon.etl not found; capture may have failed." -ForegroundColor Red
    }
} finally {
    Pop-Location
    pktmon filter remove | Out-Null
}

# --- parse ----------------------------------------------------------------
$txt = Join-Path $work "pm.txt"
$localIps = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    ForEach-Object { $_.IPAddress })
$localSet = New-Object 'System.Collections.Generic.HashSet[string]'
foreach ($ip in $localIps) { [void]$localSet.Add($ip) }

$remote = New-Object 'System.Collections.Generic.HashSet[string]'
if (Test-Path $txt) {
    foreach ($line in Get-Content $txt -Encoding UTF8) {
        if ($line -notmatch [regex]::Escape("$Port")) { continue }
        foreach ($m in [regex]::Matches($line, '\b(?:\d{1,3}\.){3}\d{1,3}\b')) {
            $ip = $m.Value
            if ($localSet.Contains($ip)) { continue }
            if ($ip -match '^(0\.|127\.|224\.|239\.|255\.)') { continue }
            [void]$remote.Add($ip)
        }
    }
}

$ips = @($remote | Sort-Object)
$cidrs = @($ips | ForEach-Object {
    $p = $_.Split('.')
    "{0}.{1}.{2}.0/24" -f $p[0], $p[1], $p[2]
} | Sort-Object -Unique)

# Verify with the pcapng payload: keep only Photon-shaped / GCM-authenticated IPs.
$pcapng = Join-Path $work "photon.pcapng"
$python = Get-Command python -ErrorAction SilentlyContinue
if ($python -and (Test-Path $pcapng)) {
    $verifyLog = Join-Path $work "verify.log"
    & $python.Source (Join-Path $PSScriptRoot "verify_photon_pcap.py") $pcapng --out $work 2>&1 |
        Tee-Object -FilePath $verifyLog | Out-Null
    $verifiedFile = Join-Path $work "verified_cidrs.txt"
    if (Test-Path $verifiedFile) {
        $verified = @(Get-Content $verifiedFile -Encoding UTF8 | Where-Object { $_.Trim() })
        if ($verified.Count -gt 0) {
            $cidrs = $verified
        } else {
            Write-Host "No packet passed Photon validation; keeping structural candidates." -ForegroundColor Yellow
        }
    }
}

$ips | Set-Content (Join-Path $work "ips.txt") -Encoding UTF8
$cidrs | Set-Content (Join-Path $work "cidrs.txt") -Encoding UTF8

$summary = @()
$summary += "region=$Region"
$summary += "port=$Port"
$summary += "captured=$stamp"
$summary += "remote_ips=$($ips.Count)"
$summary += "cidrs=$($cidrs -join ',')"
$summary | Set-Content (Join-Path $work "summary.txt") -Encoding UTF8

Write-Host ""
Write-Host ("Remote IPs: {0}" -f $ips.Count) -ForegroundColor Cyan
$ips | Select-Object -First 30 | ForEach-Object { Write-Host ("  " + $_) }
Write-Host ""
Write-Host "Candidate /24:" -ForegroundColor Cyan
$cidrs | ForEach-Object { Write-Host ("  " + $_) }
Write-Host ""
Write-Host ("Files: {0}" -f $work) -ForegroundColor Green
Write-Host ("pcapng: {0}" -f (Join-Path $work 'photon.pcapng'))

