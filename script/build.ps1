param(
    [switch]$release
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
$projectPath = Join-Path $repoRoot "src/xProtoView/xProtoView.csproj"
$runDir = Join-Path $repoRoot ".run"

$configuration = if ($release) { "Release" } else { "Debug" }

Push-Location $repoRoot
try {
    New-Item -ItemType Directory -Force -Path $runDir | Out-Null
    dotnet publish $projectPath -c $configuration -o $runDir
}
finally {
    Pop-Location
}
