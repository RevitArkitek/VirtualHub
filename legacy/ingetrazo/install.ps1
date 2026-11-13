# Installs the IngeTrazo VR plugin into the per-user plugin folder.
#   powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1
# Close IngeTrazo first (it loads plugins at startup), or restart it after.
param([switch]$NoCopyViewer)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$plugins = Join-Path $env:APPDATA "ingetrazo\plugins"
$www = Join-Path $env:APPDATA "ingetrazo\ingetrazo_vr_www"

New-Item -ItemType Directory -Force -Path $plugins | Out-Null

Copy-Item (Join-Path $root "ingetrazo_vr.py") (Join-Path $plugins "ingetrazo_vr.py") -Force
Write-Host "plugin  -> $plugins\ingetrazo_vr.py"

if (-not $NoCopyViewer) {
    if (-not (Test-Path (Join-Path $root "ingetrazo_vr_www\index.html"))) {
        throw "ingetrazo_vr_www\index.html is missing - copy RevitVR's vr-web folder here first"
    }
    New-Item -ItemType Directory -Force -Path $www | Out-Null
    Copy-Item (Join-Path $root "ingetrazo_vr_www\*") $www -Recurse -Force
    Write-Host "viewer  -> $www"
}

Write-Host ""
Write-Host "Restart IngeTrazo, then: Extensions > Export to VR"
