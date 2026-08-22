param(
    [string]$ServiceName = "HomeDashboard.Api",
    [string]$DisplayName = "HomeDashboard API",
    [string]$Port = "5000",
    [switch]$SkipFirewall
)

$ErrorActionPreference = "Stop"
$exeCandidate = Join-Path $PSScriptRoot "../api/HomeDashboard.Api.exe"
if (-not (Test-Path $exeCandidate)) {
    throw "HomeDashboard.Api.exe was not found at '$exeCandidate'. Run powershell -ExecutionPolicy Bypass -File .\tools\publish-windows.ps1 first."
}

$exePath = (Resolve-Path $exeCandidate).Path
$contentRoot = Split-Path $exePath
$binaryPath = "`"$exePath`" --urls http://0.0.0.0:$Port"

function Ensure-FirewallRule {
    $ruleName = "$ServiceName TCP $Port"
    if (Get-Command Get-NetFirewallRule -ErrorAction SilentlyContinue) {
        $existingRule = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
        if ($null -eq $existingRule) {
            New-NetFirewallRule `
                -DisplayName $ruleName `
                -Direction Inbound `
                -Action Allow `
                -Protocol TCP `
                -LocalPort $Port `
                -Profile Private `
                -Program $exePath | Out-Null
            Write-Host "Created Windows Firewall rule '$ruleName' for private networks."
        }
    }
    else {
        Write-Host "NetSecurity cmdlets were not available. If LAN access fails, allow TCP port $Port in Windows Firewall."
    }
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "Service '$ServiceName' already exists. Updating executable path and startup settings."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe config $ServiceName binPath= $binaryPath start= auto | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $contentRoot "data") -Force | Out-Null
    if (-not $SkipFirewall) {
        Ensure-FirewallRule
    }
    return
}

New-Service -Name $ServiceName -BinaryPathName $binaryPath -DisplayName $DisplayName -StartupType Automatic
sc.exe description $ServiceName "Runs the HomeDashboard web UI and API."
New-Item -ItemType Directory -Path (Join-Path $contentRoot "data") -Force | Out-Null
if (-not $SkipFirewall) {
    Ensure-FirewallRule
}
Write-Host "Created $ServiceName. Start it with: Start-Service $ServiceName"
