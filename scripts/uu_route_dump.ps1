param(
    [string]$Out = "uu_routes.csv",
    [switch]$All,
    [switch]$Wfp
)

$ErrorActionPreference = "Stop"

$uu = Get-NetAdapter -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -eq "UU" -or $_.InterfaceDescription -match "UU Wintun" } |
    Select-Object -First 1

$rows = @()
if ($uu) {
    $rows = Get-NetRoute -AddressFamily IPv4 |
        Where-Object { $_.InterfaceIndex -eq $uu.ifIndex } |
        Select-Object DestinationPrefix, NextHop, RouteMetric, ifMetric, InterfaceAlias, InterfaceIndex
    Write-Host ("UU ifIndex={0}  routes={1}" -f $uu.ifIndex, @($rows).Count)
} else {
    Write-Host "UU adapter not found; dumping all IPv4 routes for baseline."
}

if ($All -or -not $uu) {
    $rows = Get-NetRoute -AddressFamily IPv4 |
        Select-Object DestinationPrefix, NextHop, RouteMetric, ifMetric, InterfaceAlias, InterfaceIndex
}

$rows | Sort-Object DestinationPrefix |
    Export-Csv -Path $Out -NoTypeInformation -Encoding UTF8

Write-Host ("written: {0}  rows={1}" -f (Resolve-Path $Out), @($rows).Count)

if ($Wfp) {
    $xml = [System.IO.Path]::ChangeExtension($Out, ".wfp.xml")
    netsh wfp show state file="$xml" | Out-Null
    Write-Host ("wfp state: {0}" -f $xml)
}
