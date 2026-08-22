param(
    [string]$ServiceName = "HomeDashboard.Agent",
    [string]$DisplayName = "HomeDashboard Agent"
)

$ErrorActionPreference = "Stop"
$exeCandidate = Join-Path $PSScriptRoot "../agent/HomeDashboard.Agent.exe"
if (-not (Test-Path $exeCandidate)) {
    throw "HomeDashboard.Agent.exe was not found at '$exeCandidate'. Run powershell -ExecutionPolicy Bypass -File .\tools\publish-windows.ps1 first."
}

$exePath = Resolve-Path $exeCandidate
$binaryPath = "`"$exePath`""

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "Service '$ServiceName' already exists. Skipping create."
    return
}

New-Service -Name $ServiceName -BinaryPathName $binaryPath -DisplayName $DisplayName -StartupType Automatic
sc.exe description $ServiceName "Publishes Windows service and system health snapshots to HomeDashboard."
Write-Host "Created $ServiceName. Start it with: Start-Service $ServiceName"
