<#
  Builds the three release packages from a built tree.

    .\tools\package-release.ps1 -Version v0.1.0                 # server + IngeTrazo
    .\tools\package-release.ps1 -Version v0.1.0 -WithRevit      # + the Revit add-in (needs dist\)

  Each package is a folder and a zip in release\<version>\:

    virtualhub-revit-<year>-<version>.zip    add-in + server + web + install.ps1
    virtualhub-ingetrazo-<version>.zip       plugin + web + install.ps1
    virtualhub-server-<version>.zip          server + web + start-library.ps1

  The Revit package needs a locally compiled add-in (plugins/revit/build.ps1), because Autodesk's
  assemblies cannot be redistributed and therefore cannot be compiled on a hosted CI runner.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$Version,
  [string]$RevitYear = "2025",
  [switch]$WithRevit,
  [switch]$SkipServerPublish
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$out = Join-Path $root "release/$Version"
$stage = Join-Path $out "stage"
$web = Join-Path $root "web"
$shipped = Join-Path $root "tools/release"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# ---- the server ------------------------------------------------------------------
if (-not $SkipServerPublish) {
  Write-Host "==> publishing the server (self-contained, win-x64)" -ForegroundColor Cyan
  & dotnet publish (Join-Path $root "server/RevitVr.Server/RevitVr.Server.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o (Join-Path $root "dist/server") --nologo -v q
  if ($LASTEXITCODE -ne 0) { throw "server publish failed" }
}
$serverExe = Join-Path $root "dist/server/RevitVr.Server.exe"
if (-not (Test-Path $serverExe)) {
  throw "dist/server/RevitVr.Server.exe is missing. Run plugins/revit/build.ps1 first (or drop -SkipServerPublish)."
}
if (-not (Test-Path (Join-Path $web "index.html"))) { throw "web/index.html is missing" }

function Write-Readme([string]$path, [string]$text) {
  [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))
}

# ---- 1. server + library ---------------------------------------------------------
Write-Host "==> packing the standalone server" -ForegroundColor Cyan
$serverPkg = Join-Path $stage "virtualhub-server-$Version"
New-Item -ItemType Directory -Force -Path (Join-Path $serverPkg "server"), (Join-Path $serverPkg "web") | Out-Null
Copy-Item $serverExe (Join-Path $serverPkg "server") -Force
Copy-Item (Join-Path $web "*") (Join-Path $serverPkg "web") -Recurse -Force
Copy-Item (Join-Path $shipped "start-library.ps1") $serverPkg -Force
Write-Readme (Join-Path $serverPkg "README.txt") @"
VirtualHub model library $Version
================================

The library server on its own: no Revit, no IngeTrazo. It serves the homepage that lists every
model either plugin has exported, and the WebXR viewer for each.

Start it:

    .\start-library.ps1                 this PC: http://localhost:8765/
    .\start-library.ps1 -Headset        also open it on the Quest over adb

The headset over Wi-Fi uses https://<this-pc>:8766/ once (accept the certificate).
Models live in %LOCALAPPDATA%\VRHub\library; exporting from either plugin adds or replaces one.

Docs: https://github.com/<you>/VirtualHub/blob/main/docs/headset.md
"@

# ---- 2. IngeTrazo plugin ---------------------------------------------------------
Write-Host "==> packing the IngeTrazo plugin" -ForegroundColor Cyan
$ingPkg = Join-Path $stage "virtualhub-ingetrazo-$Version"
New-Item -ItemType Directory -Force -Path (Join-Path $ingPkg "plugins/ingetrazo"), (Join-Path $ingPkg "web") | Out-Null
Copy-Item (Join-Path $root "plugins/ingetrazo/ingetrazo_vr.py") (Join-Path $ingPkg "plugins/ingetrazo") -Force
Copy-Item (Join-Path $root "plugins/ingetrazo/install.ps1") (Join-Path $ingPkg "plugins/ingetrazo") -Force
Copy-Item (Join-Path $web "*") (Join-Path $ingPkg "web") -Recurse -Force
Write-Readme (Join-Path $ingPkg "README.txt") @"
VirtualHub IngeTrazo plugin $Version
====================================

Close IngeTrazo first. Then, from this folder:

    powershell -NoProfile -ExecutionPolicy Bypass -File plugins\ingetrazo\install.ps1

Restart IngeTrazo and look under Extensions > Export to VR. The installer copies the plugin to
%APPDATA%\ingetrazo\plugins and the shared web app to %APPDATA%\ingetrazo\ingetrazo_vr_www.

Exports publish to %LOCALAPPDATA%\VRHub\library and appear on the same homepage the Revit plugin
uses. IngeTrazo 0.3.x is the tested host.

Docs: https://github.com/<you>/VirtualHub/blob/main/docs/ingetrazo.md
"@

# ---- 3. Revit add-in -------------------------------------------------------------
if ($WithRevit) {
  $distAddin = Join-Path $root "dist/addin"
  if (-not (Test-Path (Join-Path $distAddin "RevitVr.Addin.dll"))) {
    throw "dist/addin is missing. Run plugins/revit/build.ps1 -NoInstall first."
  }
  Write-Host "==> packing the Revit add-in (Revit $RevitYear)" -ForegroundColor Cyan
  $revitPkg = Join-Path $stage "virtualhub-revit-$RevitYear-$Version"
  New-Item -ItemType Directory -Force -Path (Join-Path $revitPkg "addin"), (Join-Path $revitPkg "server"), (Join-Path $revitPkg "web") | Out-Null
  Copy-Item (Join-Path $distAddin "*") (Join-Path $revitPkg "addin") -Recurse -Force
  Copy-Item $serverExe (Join-Path $revitPkg "server") -Force
  Copy-Item (Join-Path $web "*") (Join-Path $revitPkg "web") -Recurse -Force
  Copy-Item (Join-Path $shipped "revit-install.ps1") (Join-Path $revitPkg "install.ps1") -Force
  Write-Readme (Join-Path $revitPkg "README.txt") @"
VirtualHub Revit plugin $Version (Revit $RevitYear)
===================================================

Close Revit. Then, from this folder:

    .\install.ps1

It copies the add-in to %APPDATA%\Autodesk\Revit\Addins\$RevitYear\RevitVR, adds the inbound
firewall rule when run as Administrator, and prints what to press next.

In Revit: Add-Ins > RevitVR > Enter VR. The add-in needs Revit $RevitYear; the package is compiled
against that version's API and will not load in another year.

Exports appear on the model library, which either plugin can serve:
    http://localhost:8765/     this PC
    https://<this-pc>:8766/    headset over Wi-Fi (accept the certificate once)

Docs: https://github.com/<you>/VirtualHub/blob/main/docs/revit.md
"@
} else {
  Write-Warning "no -WithRevit: the Revit package was not built (run plugins/revit/build.ps1 first)"
}

# ---- zips ------------------------------------------------------------------------
Write-Host "==> zipping" -ForegroundColor Cyan
Get-ChildItem $stage -Directory | ForEach-Object {
  $zip = Join-Path $out "$($_.Name).zip"
  Compress-Archive -Path $_.FullName -DestinationPath $zip -Force
  $mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
  Write-Host ("    {0,-44} {1,6} MB" -f (Split-Path $zip -Leaf), $mb) -ForegroundColor Green
}

Write-Host ""
Write-Host "release assets in $out" -ForegroundColor Green
Get-ChildItem $out -Filter *.zip | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}} | Format-Table

# Checksums so a download can be verified without trusting the release page.
Get-ChildItem $out -Filter *.zip | ForEach-Object {
  "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower())  $($_.Name)"
} | Set-Content (Join-Path $out "SHA256SUMS.txt") -Encoding ascii
Write-Host "checksums: $(Join-Path $out 'SHA256SUMS.txt')"
