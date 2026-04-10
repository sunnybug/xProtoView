param()

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir

Push-Location $repoRoot
try {
    dotnet --version | Out-Null
    dotnet restore ".\src\xProtoView\xProtoView.csproj"
    dotnet restore ".\tests\xProtoView.Tests\xProtoView.Tests.csproj"
    Write-Host "开发环境初始化完成。"
}
finally {
    Pop-Location
}
