<#
.SYNOPSIS
  Publish Naga Battery Tray (full self-contained) and install it to the per-user
  programs folder, wire it to run at login, and (re)launch it.

.DESCRIPTION
  Run this after any code change to update the installed app. It:
    1. Publishes a Release build  -> one ~188 MB exe + 5 WPF native DLLs.
    2. Stops any running instance (so the exe isn't locked).
    3. Copies the runtime files (everything except the .pdb) to
       %LOCALAPPDATA%\Programs\NagaBatteryTray.
    4. Registers a delayed logon scheduled task (run at login) and clears any
       legacy HKCU "Run" entry.
    5. Launches the installed exe.

  Run-at-login uses a Scheduled Task with a 1-minute logon delay, NOT the Run key:
  Smart App Control vetoes the unsigned exe when the Run key fires ~52 s into boot
  (the ISG cloud-reputation lookup isn't ready yet). The delayed task launches once
  the machine is online and the hash is judged reputable. See StartupRegistration.cs.

  No admin rights required - everything is per-user.

  NOTE: the 5 *_cor3.dll files MUST stay next to the exe. WPF loads them as
  native libraries; PublishSingleFile bundles the managed code but leaves these
  beside the exe. Copying the exe alone causes a DllNotFoundException at startup.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path -Parent $PSScriptRoot
$project    = Join-Path $repoRoot 'src\NagaBatteryTray'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\NagaBatteryTray'
$installExe = Join-Path $installDir 'NagaBatteryTray.exe'

# Resolve the dotnet CLI (the user-local install isn't always on PATH).
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet -and $env:DOTNET_ROOT) { $dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe' }
if (-not $dotnet -or -not (Test-Path $dotnet)) { $dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe' }
if (-not (Test-Path $dotnet)) { throw "Could not find dotnet. Set DOTNET_ROOT or add dotnet to PATH." }

Write-Host "Publishing (Release, self-contained single-file)..." -ForegroundColor Cyan
& $dotnet publish $project -c Release | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

$tfm = (Get-ChildItem (Join-Path $project 'bin\Release') -Directory | Select-Object -First 1).Name
$publish = Join-Path $project "bin\Release\$tfm\win-x64\publish"
if (-not (Test-Path (Join-Path $publish 'NagaBatteryTray.exe'))) { throw "Publish output not found at $publish" }

Write-Host "Stopping any running instance..." -ForegroundColor Cyan
Get-Process -Name 'NagaBatteryTray' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

Write-Host "Installing to $installDir ..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Get-ChildItem $publish | Where-Object { $_.Extension -ne '.pdb' } | Copy-Item -Destination $installDir -Force

Write-Host "Registering run-at-login (delayed logon scheduled task)..." -ForegroundColor Cyan
# The exe self-registers the task (single source of truth: StartupRegistration.cs).
# -Wait works for the windowless GUI exe and surfaces the exit code.
$reg = Start-Process -FilePath $installExe -ArgumentList '--enable-startup' -Wait -PassThru
if ($reg.ExitCode -ne 0) { throw "Run-at-login registration failed (exit $($reg.ExitCode))." }
# Remove the legacy Run-key entry so it doesn't keep tripping SAC at every boot.
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
  -Name 'NagaBatteryTray' -ErrorAction SilentlyContinue

Write-Host "Launching..." -ForegroundColor Cyan
Start-Process $installExe

Write-Host "Done. Installed and running from $installExe" -ForegroundColor Green
