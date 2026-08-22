param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$apiProject = Join-Path $repoRoot "src/HomeDashboard.Api/HomeDashboard.Api.csproj"
$nugetConfig = Join-Path $repoRoot "NuGet.Config"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET SDK was not found. Install the .NET 8 SDK or newer, then rerun this script."
}

Write-Host "Restoring API packages..."
dotnet restore $apiProject --configfile $nugetConfig
if ($LASTEXITCODE -ne 0) {
    throw "API package restore failed."
}

Write-Host "Building HomeDashboard API..."
dotnet build $apiProject --configuration $Configuration --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "API build failed."
}

Write-Host "HomeDashboard API build completed successfully."
