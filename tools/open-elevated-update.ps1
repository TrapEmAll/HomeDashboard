param(
    [string]$Repository = "TrapEmAll/HomeDashboard",
    [string]$Branch = "main",
    [string]$Port = "5000",
    [switch]$SelfContained,
    [switch]$NoStart,
    [switch]$UseCurrentDirectory
)

$ErrorActionPreference = "Stop"

$sourceRoot = if ($UseCurrentDirectory) {
    Resolve-Path (Get-Location)
}
else {
    Resolve-Path (Join-Path $PSScriptRoot "..")
}

$toolsRoot = Join-Path $sourceRoot "tools"
$updater = Join-Path $toolsRoot "update-homedashboard.ps1"

New-Item -ItemType Directory -Path $toolsRoot -Force | Out-Null
$rawUrl = "https://raw.githubusercontent.com/$Repository/$Branch/tools/update-homedashboard.ps1"
Write-Host "Refreshing updater from $rawUrl..."
Invoke-WebRequest -Uri $rawUrl -OutFile $updater

$arguments = @(
    "-NoExit",
    "-ExecutionPolicy", "Bypass",
    "-File", "`"$updater`"",
    "-Repository", "`"$Repository`"",
    "-Branch", "`"$Branch`"",
    "-Port", "`"$Port`""
)

if ($SelfContained) {
    $arguments += "-SelfContained"
}

if ($NoStart) {
    $arguments += "-NoStart"
}

Start-Process powershell.exe -Verb RunAs -WorkingDirectory $sourceRoot -ArgumentList $arguments
