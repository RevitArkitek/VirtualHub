<#
  Builds and installs the Revit plugin.

    .\build.ps1                      # build, assemble dist\, install for Revit 2025
    .\build.ps1 -RevitYear 2026      # retarget
    .\build.ps1 -NoInstall           # just assemble dist\

  Produces, in <repo>\dist:
    addin\                      the add-in
    server\                     self-contained local web server (no .NET install needed)
    web\                        the model-library homepage + WebXR viewer
    RevitVr.addin               manifest with the absolute assembly path filled in
#>
[CmdletBinding()]
param(
  [string]$RevitYear = "2025",
  [string]$RevitDir  = "",
  [switch]$NoInstall
)

$ErrorActionPreference = "Stop"
$pluginDir = $PSScriptRoot
$root = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
if (-not $RevitDir) { $RevitDir = "C:\Program Files\Autodesk\Revit $RevitYear" }
# Forward slashes: a trailing backslash inside -p:Name="..." escapes the closing quote and
# silently breaks the assembly reference.
$RevitDir = $RevitDir.TrimEnd('\', '/') + '/'
$RevitDirFs = $RevitDir.Replace('/', '\')

if (-not (Test-Path (Join-Path $RevitDirFs "RevitAPI.dll"))) {
  throw "RevitAPI.dll not found in '$RevitDirFs'. Pass -RevitDir explicitly."
}

# Revit 2025.5+ and Revit 2026 run on .NET 10; earlier 2025 builds were .NET 8. Read the runtime
# Revit itself declares instead of guessing: an add-in compiled for the wrong runtime fails with
# CS1705 before anything else can be checked.
$revitTfm = "net8.0-windows"
$runtimeCfg = Join-Path $RevitDirFs "RevitAPI.runtimeconfig.json"
if (Test-Path $runtimeCfg) {
  try {
    $declared = (Get-Content $runtimeCfg -Raw | ConvertFrom-Json).runtimeOptions.tfm
    if ($declared) { $revitTfm = "$declared-windows" }
  } catch { Write-Warning "could not read $runtimeCfg; assuming $revitTfm" }
}
Write-Host "==> Revit runtime: $revitTfm" -ForegroundColor Cyan

# Pick a dotnet whose SDK can target that runtime. A user-local SDK (dotnet-install.ps1 into
# ~\.dotnet) is not on PATH until the next logon, and the machine-wide SDK may be older than
# Revit's runtime — both are normal here.
$tfmMajor = [int]($revitTfm -replace '^net(\d+).*$', '$1')
$dotnet = $null
$candidates = @()
$onPath = Get-Command dotnet -ErrorAction SilentlyContinue
if ($onPath) { $candidates += $onPath.Source }
$localDotnet = Join-Path $HOME ".dotnet\dotnet.exe"
if (Test-Path $localDotnet) { $candidates += $localDotnet }

foreach ($cand in $candidates) {
  foreach ($line in @(& $cand --list-sdks 2>$null)) {
    if ($line -match '^(\d+)\.' -and [int]$Matches[1] -ge $tfmMajor) { $dotnet = $cand; break }
  }
  if ($dotnet) { break }
}
if (-not $dotnet) {
  throw "No .NET SDK found that targets $revitTfm. Install one: https://aka.ms/dotnet/download"
}
$env:PATH = "$(Split-Path $dotnet);$env:PATH"
Write-Host "==> dotnet: $dotnet" -ForegroundColor Cyan

$dist  = Join-Path $root "dist"
$addin = Join-Path $dist "addin"

Write-Host "==> cleaning $dist" -ForegroundColor Cyan
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $dist, $addin -Force | Out-Null

Write-Host "==> build core + add-in (Revit $RevitYear)" -ForegroundColor Cyan
& dotnet build (Join-Path $pluginDir "RevitVr.Addin/RevitVr.Addin.csproj") `
    -c Release -p:RevitDir="$RevitDir" -p:RevitApiVersion=$RevitYear `
    -p:TargetFramework=$revitTfm --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "add-in build failed" }

$addinOut = Join-Path $pluginDir "RevitVr.Addin/bin/Release/$revitTfm"
Copy-Item (Join-Path $addinOut "RevitVr.Addin.dll") $addin -Force
Copy-Item (Join-Path $addinOut "RevitVr.Core.dll")  $addin -Force
if (Test-Path (Join-Path $addinOut "RevitVr.Addin.deps.json")) {
  Copy-Item (Join-Path $addinOut "RevitVr.Addin.deps.json") $addin -Force
}
Copy-Item (Join-Path $addinOut "Resources") $addin -Recurse -Force

Write-Host "==> publish server (self-contained, win-x64)" -ForegroundColor Cyan
$serverOut = Join-Path $dist "server"
& dotnet publish (Join-Path $root "server/RevitVr.Server/RevitVr.Server.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $serverOut --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "server publish failed" }

Write-Host "==> copy web app" -ForegroundColor Cyan
Copy-Item (Join-Path $root "web") (Join-Path $dist "web") -Recurse -Force

Write-Host "==> write manifest" -ForegroundColor Cyan
$manifest = Get-Content (Join-Path $pluginDir "RevitVr.Addin/RevitVr.addin") -Raw
$asmPath = Join-Path $addin "RevitVr.Addin.dll"
# plain backslashes: XML needs no escaping, and doubling them breaks the path Revit resolves
$manifest = $manifest.Replace("__ASSEMBLY__", $asmPath)
[System.IO.File]::WriteAllText((Join-Path $dist "RevitVr.addin"), $manifest, (New-Object System.Text.UTF8Encoding($false)))

if (-not $NoInstall) {
  $userAddins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitYear"
  $target = Join-Path $userAddins "RevitVR"
  Write-Host "==> installing to $target" -ForegroundColor Cyan
  New-Item -ItemType Directory -Path $userAddins -Force | Out-Null
  if (Test-Path $target) { Remove-Item $target -Recurse -Force }
  New-Item -ItemType Directory -Path $target -Force | Out-Null
  Copy-Item (Join-Path $addin "*") $target -Recurse -Force
  Copy-Item $serverOut $target -Recurse -Force
  Copy-Item (Join-Path $dist "web") $target -Recurse -Force

  # the .addin itself must sit directly in Addins\<year>\
  $installedAsm = Join-Path $target "RevitVr.Addin.dll"
  $m2 = (Get-Content (Join-Path $pluginDir "RevitVr.Addin/RevitVr.addin") -Raw).Replace("__ASSEMBLY__", $installedAsm)
  [System.IO.File]::WriteAllText((Join-Path $userAddins "RevitVr.addin"), $m2, (New-Object System.Text.UTF8Encoding($false)))
  Write-Host "    installed. Restart Revit; the button is on Add-Ins > RevitVR." -ForegroundColor Green

  # Windows Firewall drops inbound connections to an unknown exe, so without this the headset
  # cannot reach the HTTPS address on the local network. The rule is per year: the server exe
  # lives inside the install folder, so allowing Revit 2025's copy does nothing for 2026's.
  $ruleName = "RevitVR (Revit $RevitYear)"
  $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
  if ($isAdmin) {
    try {
      Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
      New-NetFirewallRule -DisplayName $ruleName -Direction Inbound `
        -Program (Join-Path $target 'server\RevitVr.Server.exe') `
        -Action Allow -Profile Private, Domain | Out-Null
      Write-Host "    firewall: inbound allowed for the headset on private networks" -ForegroundColor Green
    } catch {
      Write-Warning "    could not add the firewall rule: $_"
    }
  } else {
    Write-Host "    firewall: run this script as Administrator to allow the headset through" -ForegroundColor Yellow
  }
}

# quick sanity check on the packaged server
Write-Host "==> smoke test: packaged server" -ForegroundColor Cyan
$exe = Join-Path $serverOut "RevitVr.Server.exe"
$demoScene = Join-Path $root "runtime/export"
if ((Test-Path $exe) -and (Test-Path $demoScene)) {
  $p = Start-Process -FilePath $exe -ArgumentList @("--app", (Join-Path $dist "web"), "--scene", $demoScene, "--port", "8799") -PassThru -WindowStyle Hidden
  Start-Sleep -Seconds 3
  try {
    $r = Invoke-RestMethod "http://127.0.0.1:8799/api/status" -TimeoutSec 5
    Write-Host ("    /api/status ok={0} hasScene={1} glb={2} bytes" -f $r.ok, $r.hasScene, $r.glbBytes) -ForegroundColor Green
  } catch {
    Write-Warning "    server smoke test failed: $_"
  } finally {
    try { $p | Stop-Process -Force } catch {}
  }
}

Write-Host ""
Write-Host "dist ready: $dist" -ForegroundColor Green
