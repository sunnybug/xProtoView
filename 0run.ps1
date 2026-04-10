param(
    [switch]$release,
    [switch]$test
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = $scriptDir
$runDir = Join-Path $repoRoot ".run"
$logDir = Join-Path $runDir "log"
$appName = "xProtoView"
$appExe = Join-Path $runDir "$appName.exe"

function Show-Usage {
    Write-Error "不支持的参数。支持参数：--release, --test"
}

foreach ($arg in $args) {
    if ($arg -notin @("--release", "--test")) {
        Show-Usage
        exit 1
    }
}

Push-Location $repoRoot
try {
    Get-Process -Name $appName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    if ($release) {
        & ".\script\build.ps1" --release
    }
    else {
        & ".\script\build.ps1"
    }

    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    Set-Location $runDir
    Get-ChildItem -Path $logDir -File -ErrorAction SilentlyContinue | Remove-Item -Force

    if ($test) {
        Set-Location $repoRoot
        dotnet test ".\tests\xProtoView.Tests\xProtoView.Tests.csproj" -c $(if ($release) { "Release" } else { "Debug" })
    }
    else {
        if (-not (Test-Path $appExe)) {
            throw "未找到可执行文件：$appExe"
        }
        & $appExe
    }

    $errorLogs = @()
    if (Test-Path $logDir) {
        $errorLogs += Get-ChildItem $logDir -File | Where-Object { $_.Name -match "(crash|error)" -and $_.Length -gt 0 }
        $errorLogs += Get-ChildItem $logDir -File | Where-Object { Select-String -Path $_.FullName -Pattern "error" -SimpleMatch -Quiet }
    }
    $errorLogs = $errorLogs | Sort-Object FullName -Unique
    foreach ($file in $errorLogs) {
        Write-Host $file.FullName
    }
}
finally {
    Set-Location $repoRoot
    Pop-Location
}
