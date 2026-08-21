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
$apiWwwroot = Join-Path $repoRoot "src/HomeDashboard.Api/wwwroot"
$outputRoot = Join-Path $repoRoot "outputs/HomeDashboard-Windows"
$apiOutput = Join-Path $outputRoot "api"
$agentOutput = Join-Path $outputRoot "agent"
$toolsOutput = Join-Path $outputRoot "tools"
$zipPath = Join-Path $repoRoot "outputs/HomeDashboard-Windows.zip"

Remove-Item $outputRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
Remove-Item $apiWwwroot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $apiWwwroot, $apiOutput, $agentOutput, $toolsOutput | Out-Null

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
}
finally {
    Pop-Location
}

Copy-Item (Join-Path $webRoot "dist/*") $apiWwwroot -Recurse

$selfContainedValue = if ($SelfContained) { "true" } else { "false" }
dotnet publish $apiProject --configuration $Configuration --runtime $Runtime --self-contained $selfContainedValue --output $apiOutput
dotnet publish $agentProject --configuration $Configuration --runtime $Runtime --self-contained $selfContainedValue --output $agentOutput

Copy-Item (Join-Path $PSScriptRoot "install-api-service.ps1") $toolsOutput
Copy-Item (Join-Path $PSScriptRoot "install-agent-service.ps1") $toolsOutput
Copy-Item (Join-Path $repoRoot "README.md") $outputRoot

Compress-Archive -Path (Join-Path $outputRoot "*") -DestinationPath $zipPath -Force
Write-Host "Created $zipPath"
