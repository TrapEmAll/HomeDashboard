param(
    [string]$ServiceName = "HomeDashboard.Agent",
    [string]$DisplayName = "HomeDashboard Agent"
)

$ErrorActionPreference = "Stop"
$exePath = Resolve-Path (Join-Path $PSScriptRoot "../agent/HomeDashboard.Agent.exe")
$binaryPath = "`"$exePath`""

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    throw "Service '$ServiceName' already exists. Stop and delete it first if you want to reinstall."
}

sc.exe create $ServiceName binPath= $binaryPath start= auto DisplayName= $DisplayName
sc.exe description $ServiceName "Publishes Windows service and system health snapshots to HomeDashboard."
Write-Host "Created $ServiceName. Start it with: Start-Service $ServiceName"
