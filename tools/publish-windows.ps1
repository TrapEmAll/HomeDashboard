param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$webRoot = Join-Path $repoRoot "web"
$apiProject = Join-Path $repoRoot "src/HomeDashboard.Api/HomeDashboard.Api.csproj"
$agentProject = Join-Path $repoRoot "src/HomeDashboard.Agent/HomeDashboard.Agent.csproj"
$nugetConfig = Join-Path $repoRoot "NuGet.Config"
$apiWwwroot = Join-Path $repoRoot "src/HomeDashboard.Api/wwwroot"
$operationsService = Join-Path $repoRoot "src/HomeDashboard.Api/OperationsService.cs"
$contractsFile = Join-Path $repoRoot "src/HomeDashboard.Contracts/DashboardContracts.cs"
$outputRoot = Join-Path $repoRoot "outputs/HomeDashboard-Windows"
$apiOutput = Join-Path $outputRoot "api"
$agentOutput = Join-Path $outputRoot "agent"
$toolsOutput = Join-Path $outputRoot "tools"
$zipPath = Join-Path $repoRoot "outputs/HomeDashboard-Windows.zip"

function Remove-ProjectBuildArtifacts {
    param([string]$Root)

    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $projectNames = @("HomeDashboard.Contracts", "HomeDashboard.Api", "HomeDashboard.Agent")
    foreach ($projectName in $projectNames) {
        foreach ($directoryName in @("bin", "obj")) {
            $candidate = [IO.Path]::GetFullPath((Join-Path $Root "src/$projectName/$directoryName"))
            if (-not $candidate.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Refusing to clean build artifacts outside the repository: '$candidate'."
            }
            Remove-Item -LiteralPath $candidate -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Copy-PublishPathExact {
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

if ((Test-Path $operationsService) -and
    (-not (Test-Path $contractsFile) -or -not (Select-String -LiteralPath $contractsFile -SimpleMatch "record OperationsSnapshot" -Quiet))) {
    throw "The source tree contains OperationsService.cs without its shared contracts. Run tools\update-homedashboard.ps1 again to repair the mixed source update."
}

$publishBackupRoot = Join-Path $repoRoot "backups/HomeDashboard-publish-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
$publishStatePaths = @(
    (Join-Path $apiOutput "appsettings.Local.json"),
    (Join-Path $apiOutput "data"),
    (Join-Path $agentOutput "appsettings.Local.json")
)
$hasPublishState = $null -ne ($publishStatePaths | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1)
if ($hasPublishState) {
    Write-Host "Backing up packaged user state to $publishBackupRoot..."
    Copy-PublishPathExact (Join-Path $apiOutput "appsettings.Local.json") (Join-Path $publishBackupRoot "api/appsettings.Local.json")
    Copy-PublishPathExact (Join-Path $apiOutput "data") (Join-Path $publishBackupRoot "api/data")
    Copy-PublishPathExact (Join-Path $agentOutput "appsettings.Local.json") (Join-Path $publishBackupRoot "agent/appsettings.Local.json")
}

try {
Write-Host "Cleaning stale .NET build artifacts..."
Remove-ProjectBuildArtifacts $repoRoot
Remove-Item $outputRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
Remove-Item $apiWwwroot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $apiWwwroot, $apiOutput, $agentOutput, $toolsOutput -Force | Out-Null

Push-Location $webRoot
try {
    if (Test-Path "node_modules/.bin/vite.cmd") {
        & "node_modules/.bin/vite.cmd" build
    }
    elseif (Get-Command npm -ErrorAction SilentlyContinue) {
        npm install
        npm run build
    }
    elseif (Get-Command pnpm -ErrorAction SilentlyContinue) {
        pnpm install
        pnpm run build
    }
    else {
        throw "Node package tooling was not found. Install Node.js, then rerun this script."
    }

    if ($LASTEXITCODE -ne 0) {
        throw "Web dashboard build failed."
    }
}
finally {
    Pop-Location
}

Copy-Item (Join-Path $webRoot "dist/*") $apiWwwroot -Recurse

$selfContainedValue = if ($SelfContained) { "true" } else { "false" }
dotnet restore $apiProject --configfile $nugetConfig --runtime $Runtime
if ($LASTEXITCODE -ne 0) { throw "API package restore failed." }
dotnet restore $agentProject --configfile $nugetConfig --runtime $Runtime
if ($LASTEXITCODE -ne 0) { throw "Agent package restore failed." }
dotnet publish $apiProject --configuration $Configuration --runtime $Runtime --self-contained $selfContainedValue --no-restore --output $apiOutput
if ($LASTEXITCODE -ne 0) { throw "API publish failed." }
dotnet publish $agentProject --configuration $Configuration --runtime $Runtime --self-contained $selfContainedValue --no-restore --output $agentOutput
if ($LASTEXITCODE -ne 0) { throw "Agent publish failed." }

Copy-Item (Join-Path $PSScriptRoot "install-api-service.ps1") $toolsOutput
Copy-Item (Join-Path $PSScriptRoot "install-agent-service.ps1") $toolsOutput
Copy-Item (Join-Path $PSScriptRoot "install-homedashboard.ps1") $toolsOutput
Copy-Item (Join-Path $PSScriptRoot "update-homedashboard.ps1") $toolsOutput
Copy-Item (Join-Path $PSScriptRoot "open-elevated-update.ps1") $toolsOutput
Copy-Item (Join-Path $repoRoot "README.md") $outputRoot

Compress-Archive -Path (Join-Path $outputRoot "*") -DestinationPath $zipPath -Force
Write-Host "Created $zipPath"
}
finally {
    if ($hasPublishState) {
        Copy-PublishPathExact (Join-Path $publishBackupRoot "api/appsettings.Local.json") (Join-Path $apiOutput "appsettings.Local.json")
        Copy-PublishPathExact (Join-Path $publishBackupRoot "api/data") (Join-Path $apiOutput "data")
        Copy-PublishPathExact (Join-Path $publishBackupRoot "agent/appsettings.Local.json") (Join-Path $agentOutput "appsettings.Local.json")
        Write-Host "Packaged user state restored. Backup retained at $publishBackupRoot."
    }
}
