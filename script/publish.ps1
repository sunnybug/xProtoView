param()

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
$projectFile = Join-Path $repoRoot "src/xProtoView/xProtoView.csproj"
[xml]$proj = Get-Content $projectFile -Raw
$versionNode = $proj.Project.PropertyGroup.Version | Select-Object -First 1
$version = if ($versionNode) { $versionNode } else { "0.1.0" }

$distDir = Join-Path $repoRoot ".dist"
$outputDir = Join-Path $distDir "xProtoView-$version"

Push-Location $repoRoot
try {
    & (Join-Path $scriptDir "build.ps1") --release
    if (Test-Path $outputDir) {
        Remove-Item $outputDir -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
    Copy-Item (Join-Path $repoRoot ".run/*") $outputDir -Recurse -Force
    Write-Host "发布完成：$outputDir"
}
finally {
    Pop-Location
}
