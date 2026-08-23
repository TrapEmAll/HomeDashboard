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

    Copy-PathExact $Path $Destination
}

function Restore-IfExists {
    param(
        [string]$Path,
        [string]$Destination
    )

    Copy-PathExact $Path $Destination
}

function Copy-PathExact {
    param(
        [string]$Path,
        [string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $item = Get-Item -LiteralPath $Path
    if (-not $item.PSIsContainer) {
        New-Item -ItemType Directory -Path (Split-Path $Destination) -Force | Out-Null
        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Force
        return
    }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $sourcePath = $item.FullName.TrimEnd('\')
    $sourcePrefixLength = $sourcePath.Length + 1
    Get-ChildItem -LiteralPath $sourcePath -Recurse -File | ForEach-Object {
        $relativePath = $_.FullName.Substring($sourcePrefixLength)
        $destinationFile = Join-Path $Destination $relativePath
        New-Item -ItemType Directory -Path (Split-Path $destinationFile) -Force | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $destinationFile -Force
    }
}

function Save-UserState {
    param(
        [string]$SourceRoot,
        [string]$ApiOutput,
        [string]$AgentOutput,
        [string]$BackupRoot
    )

    Save-IfExists (Join-Path $ApiOutput "appsettings.Local.json") (Join-Path $BackupRoot "package/api/appsettings.Local.json")
    Save-IfExists (Join-Path $ApiOutput "data") (Join-Path $BackupRoot "package/api/data")
    Save-IfExists (Join-Path $AgentOutput "appsettings.Local.json") (Join-Path $BackupRoot "package/agent/appsettings.Local.json")
    Save-IfExists (Join-Path $SourceRoot "src/HomeDashboard.Api/appsettings.Local.json") (Join-Path $BackupRoot "source/api/appsettings.Local.json")
    Save-IfExists (Join-Path $SourceRoot "src/HomeDashboard.Api/data") (Join-Path $BackupRoot "source/api/data")
    Save-IfExists (Join-Path $SourceRoot "src/HomeDashboard.Agent/appsettings.Local.json") (Join-Path $BackupRoot "source/agent/appsettings.Local.json")
}

function Restore-UserState {
    param(
        [string]$SourceRoot,
        [string]$ApiOutput,
        [string]$AgentOutput,
        [string]$BackupRoot
    )

    Restore-IfExists (Join-Path $BackupRoot "package/api/appsettings.Local.json") (Join-Path $ApiOutput "appsettings.Local.json")
    Restore-IfExists (Join-Path $BackupRoot "package/api/data") (Join-Path $ApiOutput "data")
    Restore-IfExists (Join-Path $BackupRoot "package/agent/appsettings.Local.json") (Join-Path $AgentOutput "appsettings.Local.json")
    Restore-IfExists (Join-Path $BackupRoot "source/api/appsettings.Local.json") (Join-Path $SourceRoot "src/HomeDashboard.Api/appsettings.Local.json")
    Restore-IfExists (Join-Path $BackupRoot "source/api/data") (Join-Path $SourceRoot "src/HomeDashboard.Api/data")
    Restore-IfExists (Join-Path $BackupRoot "source/agent/appsettings.Local.json") (Join-Path $SourceRoot "src/HomeDashboard.Agent/appsettings.Local.json")
}

function Sync-SourceTree {
    param(
        [string]$Source,
        [string]$Destination
    )

    $sourcePath = (Resolve-Path $Source).Path.TrimEnd('\')
    $destinationPath = (Resolve-Path $Destination).Path.TrimEnd('\')
    $sourcePrefixLength = $sourcePath.Length + 1
    $copied = 0

    Get-ChildItem -LiteralPath $sourcePath -Recurse -File | ForEach-Object {
        $relativePath = $_.FullName.Substring($sourcePrefixLength)
        $destinationFile = Join-Path $destinationPath $relativePath
        $destinationDirectory = Split-Path $destinationFile
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $destinationFile -Force
        $copied++
    }

    if ($copied -eq 0) {
        throw "Downloaded archive did not contain any source files."
    }

    Write-Host "Synchronized $copied source files."
}

function Assert-SourceConsistency {
    param([string]$Root)

    $operationsService = Join-Path $Root "src/HomeDashboard.Api/OperationsService.cs"
    $contracts = Join-Path $Root "src/HomeDashboard.Contracts/DashboardContracts.cs"
    if ((Test-Path $operationsService) -and
        (-not (Test-Path $contracts) -or -not (Select-String -LiteralPath $contracts -SimpleMatch "record OperationsSnapshot" -Quiet))) {
        throw "The downloaded source archive is inconsistent: operations API contracts are missing. Download main again and retry."
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
$previousPackageRoot = Join-Path $tempRoot "previous-package"
$backupRoot = Join-Path $sourceRoot "backups/HomeDashboard-update-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
$zipUrl = "https://github.com/$Repository/archive/refs/heads/$Branch.zip"

New-Item -ItemType Directory -Path $tempRoot, $extractRoot, $backupRoot -Force | Out-Null
Set-Content -LiteralPath (Join-Path $backupRoot "BACKUP-INFO.txt") -Value @(
    "HomeDashboard user-state backup"
    "Created: $(Get-Date -Format 'o')"
    "Source: $Repository/$Branch"
    "Restore by copying package/api and package/agent into outputs/HomeDashboard-Windows."
)

try {
    Write-Host "Saving user configuration and data to $backupRoot..."
    Save-UserState $sourceRoot $apiOutput $agentOutput $backupRoot
    Save-IfExists (Join-Path $sourceRoot "outputs/HomeDashboard-Windows") $previousPackageRoot

    Stop-ExistingService $ApiServiceName
    Stop-ExistingService $AgentServiceName

    Write-Host "Downloading $zipUrl..."
    Invoke-WebRequest -Uri $zipUrl -OutFile $downloadZip
    Expand-Archive -Path $downloadZip -DestinationPath $extractRoot -Force

    $downloadedRoot = Get-ChildItem -Path $extractRoot -Directory | Select-Object -First 1
    if ($null -eq $downloadedRoot) {
        throw "Downloaded archive did not contain a source folder."
    }

    Assert-SourceConsistency $downloadedRoot.FullName
    Write-Host "Updating source files..."
    Sync-SourceTree $downloadedRoot.FullName $sourceRoot
    Assert-SourceConsistency $sourceRoot

    Write-Host "Publishing Windows package..."
    & $publishScript -SelfContained:$SelfContained

    Restore-UserState $sourceRoot $apiOutput $agentOutput $backupRoot

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
    Write-Host "User-state backup retained at $backupRoot."
}
catch {
    $updateError = $_
    Write-Warning "Update failed. Restoring the previous package and user state."
    try {
        Restore-IfExists $previousPackageRoot (Join-Path $sourceRoot "outputs/HomeDashboard-Windows")
        Restore-UserState $sourceRoot $apiOutput $agentOutput $backupRoot
        if (-not $NoStart) {
            Start-ExistingService $ApiServiceName
            Start-ExistingService $AgentServiceName
        }
        Write-Warning "Rollback completed. The durable user-state backup remains at '$backupRoot'."
    }
    catch {
        Write-Warning "Automatic rollback encountered an error: $($_.Exception.Message)"
        Write-Warning "The durable user-state backup remains at '$backupRoot'."
    }
    throw $updateError
}
finally {
    Remove-Item $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
