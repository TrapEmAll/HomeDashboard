param(
    [switch]$Api,
    [switch]$Agent,
    [string]$Port = "5000"
)

$ErrorActionPreference = "Stop"

if (-not $Api -and -not $Agent) {
    $Api = $true
    $Agent = $true
}

if ($Api) {
    & (Join-Path $PSScriptRoot "install-api-service.ps1") -Port $Port
}

if ($Agent) {
    & (Join-Path $PSScriptRoot "install-agent-service.ps1")
}

Write-Host "HomeDashboard install steps completed."
