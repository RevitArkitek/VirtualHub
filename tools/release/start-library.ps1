<#
  Starts the VirtualHub model library from this package — no Revit, no IngeTrazo.

    .\start-library.ps1              start on :8765 and open it in this PC's browser
    .\start-library.ps1 -Headset     also open it on the Quest (adb, or the https address)
    .\start-library.ps1 -NoBrowser   just start it
    .\start-library.ps1 -Stop        stop the server this script started

  http://localhost:<port>/ for this PC and adb; https://<lan-ip>:<port+1>/ for the headset.
#>
[CmdletBinding()]
param(
  [int]$Port = 8765,
  [switch]$Headset,
  [switch]$NoBrowser,
  [switch]$Stop
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$exe = Join-Path $root "server/RevitVr.Server.exe"
$web = Join-Path $root "web"
$library = Join-Path $env:LOCALAPPDATA "VRHub/library"
$scene = Join-Path $env:LOCALAPPDATA "RevitVR/export"

if ($Stop) {
  $stopped = 0
  Get-CimInstance Win32_Process -Filter "Name='RevitVr.Server.exe'" | ForEach-Object {
    if ($_.CommandLine -match "--library") { Stop-Process -Id $_.ProcessId -Force; $stopped++ }
  }
  Write-Host "stopped $stopped library server(s)"
  return
}

foreach ($needed in @("server/RevitVr.Server.exe", "web/index.html")) {
  if (-not (Test-Path (Join-Path $root $needed))) {
    throw "This package is incomplete: $needed is missing. Download the zip again."
  }
}
New-Item -ItemType Directory -Force -Path $library, $scene | Out-Null

function Test-LibraryServer([int]$p) {
  try {
    $s = Invoke-RestMethod "http://127.0.0.1:$p/api/status" -TimeoutSec 1
    return ($s.ok -eq $true -and $null -ne $s.library)
  } catch { return $false }
}

if (-not (Test-LibraryServer $Port)) {
  Start-Process -FilePath $exe -WindowStyle Hidden -ArgumentList @(
    "--app", $web, "--scene", $scene, "--library", $library, "--port", "$Port") | Out-Null
  $deadline = (Get-Date).AddSeconds(25)
  while ((Get-Date) -lt $deadline -and -not (Test-LibraryServer $Port)) { Start-Sleep -Milliseconds 250 }
  if (-not (Test-LibraryServer $Port)) { throw "the server did not answer on port $Port" }
}

$status = Invoke-RestMethod "http://127.0.0.1:$Port/api/status" -TimeoutSec 3
$url = "http://localhost:$Port/"
Write-Host "model library - $($status.models) model(s)"
Write-Host "  this PC  $url"
if ([int]$status.httpsPort -gt 0) {
  $ip = (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -like '192.168.*' -and $_.InterfaceAlias -notmatch 'VPN|NordLynx|TAP' } |
    Select-Object -First 1).IPAddress
  if ($ip) { Write-Host "  headset  https://${ip}:$($status.httpsPort)/   (accept the certificate once)" }
}

if (-not $NoBrowser) { Start-Process $url | Out-Null }

if ($Headset) {
  $adb = @(
    (Get-Command adb -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
    (Join-Path $env:LOCALAPPDATA "Android/Sdk/platform-tools/adb.exe")
  ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
  $serial = if ($adb) { (& $adb devices) | Select-String "device$" | ForEach-Object { ($_ -split "\s+")[0] } | Select-Object -First 1 }
  if ($serial) {
    & $adb -s $serial reverse "tcp:$Port" "tcp:$Port" | Out-Null
    & $adb -s $serial shell am start -a android.intent.action.VIEW -d $url | Out-Null
    Write-Host "opened on $serial (adb)"
  } else {
    Write-Host "no headset over adb - open the https address above in the Quest browser"
  }
}
