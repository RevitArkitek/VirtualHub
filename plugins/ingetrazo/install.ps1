# Installs the IngeTrazo VR plugin into the per-user plugin folder.
#   powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1
# Close IngeTrazo first (it loads plugins at startup), or restart it after.
param([switch]$NoCopyViewer)

$ErrorActionPreference = "Stop"
$pluginDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = (Resolve-Path (Join-Path $pluginDir "../..")).Path
$web = Join-Path $repo "web"
$plugins = Join-Path $env:APPDATA "ingetrazo\plugins"
$www = Join-Path $env:APPDATA "ingetrazo\ingetrazo_vr_www"

New-Item -ItemType Directory -Force -Path $plugins | Out-Null

Copy-Item (Join-Path $pluginDir "ingetrazo_vr.py") (Join-Path $plugins "ingetrazo_vr.py") -Force
Write-Host "plugin  -> $plugins\ingetrazo_vr.py"

if (-not $NoCopyViewer) {
    # The web app is shared with the Revit plugin; "ingetrazo_vr_www" is the runtime folder name the
    # plugin looks for, and stays that way so existing installs keep working.
    if (-not (Test-Path (Join-Path $web "index.html"))) {
        throw "web/index.html is missing - the shared web app lives at <repo>/web"
    }
    New-Item -ItemType Directory -Force -Path $www | Out-Null
    Copy-Item (Join-Path $web "*") $www -Recurse -Force
    Write-Host "web app -> $www"
}

Write-Host ""
Write-Host "Restart IngeTrazo, then: Extensions > Export to VR"
