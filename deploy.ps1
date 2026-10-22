$ErrorActionPreference = "Stop"

$distDir = Join-Path $PSScriptRoot "dist"
$targetDir = Join-Path $env:LOCALAPPDATA "Logi\LogiPluginService\Plugins\AudioSwitcher"
$serviceExe = "C:\Program Files\Logi\LogiPluginService\LogiPluginService.exe"

if (-not (Test-Path $distDir)) {
    throw "dist directory does not exist. Please run build.ps1 first."
}

Write-Host "Stopping LogiPluginService..." -ForegroundColor Yellow
Stop-Process -Name LogiPluginService, LogiPluginServiceExt -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

Write-Host "Deploying files to $targetDir..." -ForegroundColor Cyan
if (-not (Test-Path $targetDir)) {
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
}

Copy-Item -Path "$distDir\*" -Destination $targetDir -Recurse -Force
Write-Host "Copied successfully." -ForegroundColor Green

if (Test-Path $serviceExe) {
    Write-Host "Starting LogiPluginService..." -ForegroundColor Yellow
    Start-Process -FilePath $serviceExe -ArgumentList "-noui"
    Start-Sleep -Seconds 3
    
    $proc = Get-Process -Name LogiPluginService -ErrorAction SilentlyContinue
    if ($proc) {
        Write-Host "LogiPluginService is running (PID: $($proc.Id))." -ForegroundColor Green
    } else {
        Write-Host "Warning: LogiPluginService did not start." -ForegroundColor Red
    }
} else {
    Write-Host "Service executable not found at $serviceExe." -ForegroundColor Red
}
