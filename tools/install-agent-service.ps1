param(
    [string]$ServiceName = "HomeDashboard.Agent",
    [string]$DisplayName = "HomeDashboard Agent",
    [string]$Port = "5000"
)

$ErrorActionPreference = "Stop"
$exeCandidate = Join-Path $PSScriptRoot "../agent/HomeDashboard.Agent.exe"
if (-not (Test-Path $exeCandidate)) {
    throw "HomeDashboard.Agent.exe was not found at '$exeCandidate'. Run powershell -ExecutionPolicy Bypass -File .\tools\publish-windows.ps1 first."
}

$exePath = Resolve-Path $exeCandidate
$binaryPath = "`"$exePath`""

function Set-JsonProperty {
    param($Object, [string]$Name, $Value)
    $Object | Add-Member -MemberType NoteProperty -Name $Name -Value $Value -Force
}

function Sync-AgentConfiguration {
    param(
        [string]$PackagedToolsRoot = $PSScriptRoot,
        [string]$ApiPort = $Port
    )
    $apiSettingsPath = Join-Path $PackagedToolsRoot "../api/appsettings.Local.json"
    $agentSettingsPath = Join-Path $PackagedToolsRoot "../agent/appsettings.Local.json"
    if (-not (Test-Path -LiteralPath $apiSettingsPath)) {
        Write-Host "API local settings do not exist yet; agent credentials will synchronize when dashboard setup is saved."
        return
    }

    $apiSettings = Get-Content -LiteralPath $apiSettingsPath -Raw | ConvertFrom-Json
    $agentApiKey = $apiSettings.Security.AgentApiKey
    if ([string]::IsNullOrWhiteSpace($agentApiKey) -or $agentApiKey.StartsWith("change-me", [StringComparison]::OrdinalIgnoreCase)) {
        Write-Host "API agent credentials are not configured yet; skipping agent credential synchronization."
        return
    }

    $agentSettings = if (Test-Path -LiteralPath $agentSettingsPath) {
        Get-Content -LiteralPath $agentSettingsPath -Raw | ConvertFrom-Json
    }
    else {
        [pscustomobject]@{}
    }
    if ($null -eq $agentSettings.Agent) {
        Set-JsonProperty $agentSettings "Agent" ([pscustomobject]@{})
    }

    $agentId = if ([string]::IsNullOrWhiteSpace($apiSettings.Dashboard.DefaultAgentId)) { "server-pc" } else { $apiSettings.Dashboard.DefaultAgentId }
    Set-JsonProperty $agentSettings.Agent "AgentId" $agentId
    Set-JsonProperty $agentSettings.Agent "DashboardApiUrl" "http://localhost:$ApiPort"
    Set-JsonProperty $agentSettings.Agent "ApiKey" $agentApiKey
    $temporaryPath = "$agentSettingsPath.$([Guid]::NewGuid().ToString('n')).tmp"
    try {
        $agentSettings | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $temporaryPath -Encoding UTF8
        Move-Item -LiteralPath $temporaryPath -Destination $agentSettingsPath -Force
    }
    finally {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    }
    Write-Host "Synchronized agent credentials and API address from the dashboard configuration."
}

Sync-AgentConfiguration

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "Service '$ServiceName' already exists. Updating executable path and startup settings."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe config $ServiceName binPath= $binaryPath start= auto | Out-Null
    return
}

New-Service -Name $ServiceName -BinaryPathName $binaryPath -DisplayName $DisplayName -StartupType Automatic
sc.exe description $ServiceName "Publishes Windows service and system health snapshots to HomeDashboard."
Write-Host "Created $ServiceName. Start it with: Start-Service $ServiceName"
