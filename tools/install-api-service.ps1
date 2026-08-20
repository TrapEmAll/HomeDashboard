param(
    [string]$ServiceName = "HomeDashboard.Api",
    [string]$DisplayName = "HomeDashboard API",
    [string]$Port = "5000"
)

$ErrorActionPreference = "Stop"
$exePath = Resolve-Path (Join-Path $PSScriptRoot "../api/HomeDashboard.Api.exe")
$contentRoot = Split-Path $exePath
$binaryPath = "`"$exePath`" --urls http://0.0.0.0:$Port"

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    throw "Service '$ServiceName' already exists. Stop and delete it first if you want to reinstall."
}

sc.exe create $ServiceName binPath= $binaryPath start= auto DisplayName= $DisplayName
sc.exe description $ServiceName "Runs the HomeDashboard web UI and API."
New-Item -ItemType Directory -Path (Join-Path $contentRoot "data") -Force | Out-Null
Write-Host "Created $ServiceName. Start it with: Start-Service $ServiceName"
