param(
    [string]$ServiceName = "HomeDashboard.Api",
    [string]$DisplayName = "HomeDashboard API",
    [string]$Port = "5000"
)

$ErrorActionPreference = "Stop"
$exeCandidate = Join-Path $PSScriptRoot "../api/HomeDashboard.Api.exe"
if (-not (Test-Path $exeCandidate)) {
    throw "HomeDashboard.Api.exe was not found at '$exeCandidate'. Run powershell -ExecutionPolicy Bypass -File .\tools\publish-windows.ps1 first."
}

$exePath = Resolve-Path $exeCandidate
$contentRoot = Split-Path $exePath
$binaryPath = "`"$exePath`" --urls http://0.0.0.0:$Port"

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "Service '$ServiceName' already exists. Skipping create."
    return
}

New-Service -Name $ServiceName -BinaryPathName $binaryPath -DisplayName $DisplayName -StartupType Automatic
sc.exe description $ServiceName "Runs the HomeDashboard web UI and API."
New-Item -ItemType Directory -Path (Join-Path $contentRoot "data") -Force | Out-Null
Write-Host "Created $ServiceName. Start it with: Start-Service $ServiceName"
