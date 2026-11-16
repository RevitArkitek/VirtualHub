<#
  Installs the VirtualHub Revit plugin from this package.
  Run it from the folder you unzipped; nothing else is needed.

    .\install.ps1                    # install for Revit 2025
    .\install.ps1 -RevitYear 2026    # install for another installed Revit
    .\install.ps1 -NoFirewall        # skip the inbound firewall rule

  Revit must be closed: it keeps the add-in DLL loaded while it runs.
#>
[CmdletBinding()]
param(
  [string]$RevitYear = "2025",
  [string]$Destination = "",
  [switch]$NoFirewall
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

foreach ($needed in @("addin/RevitVr.Addin.dll", "server/RevitVr.Server.exe", "web/index.html")) {
  if (-not (Test-Path (Join-Path $root $needed))) {
    throw "This package is incomplete: $needed is missing. Download the zip again."
  }
}

if (-not $Destination) {
  $Destination = Join-Path $env:APPDATA "Autodesk/Revit/Addins/$RevitYear/RevitVR"
}
$addinsDir = Split-Path -Parent $Destination
New-Item -ItemType Directory -Force -Path $addinsDir | Out-Null

if (Get-Process -Name "Revit" -ErrorAction SilentlyContinue) {
  Write-Warning "Revit appears to be running. Close it first, or the add-in will not load until you restart it."
}

if (Test-Path $Destination) { Remove-Item $Destination -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
Copy-Item (Join-Path $root "addin/*") $Destination -Recurse -Force
Copy-Item (Join-Path $root "server") $Destination -Recurse -Force
Copy-Item (Join-Path $root "web") $Destination -Recurse -Force

$assembly = Join-Path $Destination "RevitVr.Addin.dll"
$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>RevitVR</Name>
    <Assembly>$assembly</Assembly>
    <AddInId>7c1f4a2e-93b5-4d18-9f60-2a7c5e0b8d41</AddInId>
    <FullClassName>RevitVr.Addin.App</FullClassName>
    <VendorId>RVVR</VendorId>
    <VendorDescription>RevitVR - walk your Revit model on a Quest 3</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
[System.IO.File]::WriteAllText((Join-Path $addinsDir "RevitVr.addin"), $manifest,
  (New-Object System.Text.UTF8Encoding($false)))

Write-Host "installed -> $Destination"

if (-not $NoFirewall) {
  $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
  if ($isAdmin) {
    try {
      Get-NetFirewallRule -DisplayName 'VirtualHub' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
      New-NetFirewallRule -DisplayName 'VirtualHub' -Direction Inbound `
        -Program (Join-Path $Destination 'server/RevitVr.Server.exe') `
        -Action Allow -Profile Private, Domain | Out-Null
      Write-Host "firewall  -> inbound allowed for the headset on private networks"
    } catch {
      Write-Warning "could not add the firewall rule: $_"
    }
  } else {
    Write-Host "firewall  -> run once as Administrator to let the headset connect over Wi-Fi"
  }
}

Write-Host ""
Write-Host "Restart Revit, then: Add-Ins > RevitVR > Enter VR"
Write-Host "The model library is also served without Revit while RevitVR is installed."
