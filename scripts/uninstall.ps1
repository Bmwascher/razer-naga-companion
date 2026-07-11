<#
.SYNOPSIS
  Remove Naga Battery Tray: stop it, unregister run-at-login, delete the install folder.
  Leaves settings at %APPDATA%\NagaBatteryTray untouched (delete that yourself if desired).
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\NagaBatteryTray'

Write-Host "Stopping app..." -ForegroundColor Cyan
Get-Process -Name 'NagaBatteryTray' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

Write-Host "Removing run-at-login..." -ForegroundColor Cyan
# Delete the logon scheduled task (matches the name in StartupRegistration.cs).
schtasks /Delete /TN 'NagaBatteryTray' /F 2>$null | Out-Null
# Also clear any legacy Run-key entry from older installs.
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
  -Name 'NagaBatteryTray' -ErrorAction SilentlyContinue

Write-Host "Deleting $installDir ..." -ForegroundColor Cyan
if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force }

Write-Host "Uninstalled. (Settings at %APPDATA%\NagaBatteryTray were left in place.)" -ForegroundColor Green
