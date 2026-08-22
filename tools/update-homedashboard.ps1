param(
    [string]$Repository = "TrapEmAll/HomeDashboard",
    [string]$Branch = "main",
    [string]$Port = "5000",
    [string]$ApiServiceName = "HomeDashboard.Api",
    [string]$AgentServiceName = "HomeDashboard.Agent",
    [switch]$SelfContained,
    [switch]$NoStart
)

$ErrorActionPreference = "Stop"

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this script from an elevated PowerShell window."
    }
}

function Stop-ExistingService {
    param([string]$Name)

    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($null -ne $service -and $service.Status -ne "Stopped") {
        Write-Host "Stopping $Name..."
        Stop-Service -Name $Name -Force
        $service.WaitForStatus("Stopped", "00:00:30")
    }
}

function Start-ExistingService {
    param([string]$Name)

    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($null -ne $service -and $service.Status -ne "Running") {
        Write-Host "Starting $Name..."
        Start-Service -Name $Name
    }
}

function Save-IfExists {
    param(
        [string]$Path,
        [string]$Destination
    )

    if (Test-Path $Path) {
        $parent = Split-Path $Destination
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        Copy-Item $Path $Destination -Recurse -Force
    }
}

function Restore-IfExists {
    param(
        [string]$Path,
        [string]$Destination
    )

    if (Test-Path $Path) {
        $parent = Split-Path $Destination
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        Copy-Item $Path $Destination -Recurse -Force
    }
}

Assert-Administrator

$sourceRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$publishScript = Join-Path $sourceRoot "tools/publish-windows.ps1"
$installScript = Join-Path $sourceRoot "outputs/HomeDashboard-Windows/tools/install-homedashboard.ps1"
$apiOutput = Join-Path $sourceRoot "outputs/HomeDashboard-Windows/api"
$agentOutput = Join-Path $sourceRoot "outputs/HomeDashboard-Windows/agent"

if (-not (Test-Path $publishScript)) {
    throw "This updater must be run from a HomeDashboard source checkout or source zip."
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) "HomeDashboard-update-$([Guid]::NewGuid().ToString('n'))"
$downloadZip = Join-Path $tempRoot "source.zip"
$extractRoot = Join-Path $tempRoot "extract"
$backupRoot = Join-Path $tempRoot "backup"
$zipUrl = "https://github.com/$Repository/archive/refs/heads/$Branch.zip"

New-Item -ItemType Directory -Path $tempRoot, $extractRoot, $backupRoot -Force | Out-Null

try {
    Save-IfExists (Join-Path $apiOutput "appsettings.Local.json") (Join-Path $backupRoot "api/appsettings.Local.json")
    Save-IfExists (Join-Path $apiOutput "data") (Join-Path $backupRoot "api/data")
    Save-IfExists (Join-Path $agentOutput "appsettings.Local.json") (Join-Path $backupRoot "agent/appsettings.Local.json")

    Stop-ExistingService $ApiServiceName
    Stop-ExistingService $AgentServiceName

    Write-Host "Downloading $zipUrl..."
    Invoke-WebRequest -Uri $zipUrl -OutFile $downloadZip
    Expand-Archive -Path $downloadZip -DestinationPath $extractRoot -Force

    $downloadedRoot = Get-ChildItem -Path $extractRoot -Directory | Select-Object -First 1
    if ($null -eq $downloadedRoot) {
        throw "Downloaded archive did not contain a source folder."
    }

    Write-Host "Updating source files..."
    Copy-Item (Join-Path $downloadedRoot.FullName "*") $sourceRoot -Recurse -Force

    Write-Host "Publishing Windows package..."
    & $publishScript -SelfContained:$SelfContained

    Restore-IfExists (Join-Path $backupRoot "api/appsettings.Local.json") (Join-Path $apiOutput "appsettings.Local.json")
    Restore-IfExists (Join-Path $backupRoot "api/data") (Join-Path $apiOutput "data")
    Restore-IfExists (Join-Path $backupRoot "agent/appsettings.Local.json") (Join-Path $agentOutput "appsettings.Local.json")

    if (-not (Test-Path $installScript)) {
        throw "Packaged installer was not created at '$installScript'."
    }

    Write-Host "Reinstalling Windows services..."
    & $installScript -Port $Port -SkipPublish

    if (-not $NoStart) {
        Start-ExistingService $ApiServiceName
        Start-ExistingService $AgentServiceName
    }

    Write-Host "HomeDashboard updated from $Repository/$Branch."
}
finally {
    Remove-Item $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
