param(
    [switch]$Api,
    [switch]$Agent,
    [string]$Port = "5000",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"

$sourceRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$packagedRoot = Join-Path $sourceRoot "outputs/HomeDashboard-Windows"
$packagedTools = Join-Path $packagedRoot "tools"
$packagedInstaller = Join-Path $packagedTools "install-homedashboard.ps1"
$apiExe = Join-Path $packagedRoot "api/HomeDashboard.Api.exe"
$agentExe = Join-Path $packagedRoot "agent/HomeDashboard.Agent.exe"

if ((Test-Path (Join-Path $PSScriptRoot "../api/HomeDashboard.Api.exe")) -and (Test-Path (Join-Path $PSScriptRoot "../agent/HomeDashboard.Agent.exe"))) {
    $packagedTools = $PSScriptRoot
}
elseif (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot "publish-windows.ps1")
    if (Test-Path $packagedInstaller) {
        & $packagedInstaller -Api:$Api -Agent:$Agent -Port $Port -SkipPublish
        return
    }
}

if (-not (Test-Path $apiExe) -and -not (Test-Path (Join-Path $packagedTools "../api/HomeDashboard.Api.exe"))) {
    throw "HomeDashboard.Api.exe was not found. Run tools\publish-windows.ps1 first, then run outputs\HomeDashboard-Windows\tools\install-homedashboard.ps1."
}

if (-not (Test-Path $agentExe) -and -not (Test-Path (Join-Path $packagedTools "../agent/HomeDashboard.Agent.exe"))) {
    throw "HomeDashboard.Agent.exe was not found. Run tools\publish-windows.ps1 first, then run outputs\HomeDashboard-Windows\tools\install-homedashboard.ps1."
}

if (-not $Api -and -not $Agent) {
    $Api = $true
    $Agent = $true
}

if ($Api) {
    & (Join-Path $packagedTools "install-api-service.ps1") -Port $Port
}

if ($Agent) {
    & (Join-Path $packagedTools "install-agent-service.ps1")
}

Write-Host "HomeDashboard install steps completed."
