<#
  Starts the model library server without Revit or IngeTrazo.

    .\serve-library.ps1              start on :8765 and open it in this PC's browser
    .\serve-library.ps1 -Headset     also open it on the Quest (adb, or the https address)
    .\serve-library.ps1 -Port 8765   another port
    .\serve-library.ps1 -Stop        stop a library server this script started
    .\serve-library.ps1 -InstallShortcut   add Model Library to the desktop and Start menu

  The server listens twice: http on 127.0.0.1:<port> (this PC, and the headset over adb reverse)
  and https on 0.0.0.0:<port+1> (the headset over Wi-Fi, where WebXR needs a secure origin).
  Both serve the same library, so the headset and this PC are never looking at different pages.

  The server is RevitVR's own static server (self-contained; no .NET install needed). It reads the
  shared library, so the page lists every model either plugin has exported, whether or not either
  program is open. Exporting later from Revit or IngeTrazo replaces a card here; just refresh.

  Safe to run twice: if a library server is already answering on the port, this opens it instead of
  starting a second one.
#>
[CmdletBinding()]
param(
  [int]$Port = 8765,
  [switch]$Headset,
  [switch]$Lan,
  [switch]$NoBrowser,
  [switch]$Stop,
  [switch]$InstallShortcut
)

$ErrorActionPreference = "Stop"

# Double-clicked from a desktop shortcut there is no console to read an error in, and a shortcut
# that silently does nothing is the worst outcome. Say it in a box instead.
trap {
  $msg = $_.Exception.Message
  try { (New-Object -ComObject WScript.Shell).Popup("$msg", 0, "Model Library", 16) | Out-Null } catch { }
  exit 1
}

$localAppData = [Environment]::GetFolderPath("LocalApplicationData")
$library = Join-Path $localAppData "VRHub\library"
$scene   = Join-Path $localAppData "RevitVR\export"

# ---- find the pieces ---------------------------------------------------------
# Installed copy first: that is the one the Revit add-in launches, so reusing it means one server
# and one port no matter how it was started.
$addinDir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\2025\RevitVR"
$candidates = @(
  (Join-Path $addinDir "server\RevitVr.Server.exe"),
  (Join-Path $PSScriptRoot "..\dist\server\RevitVr.Server.exe"),
  (Join-Path $PSScriptRoot "..\server\RevitVr.Server\bin\Release\net9.0\RevitVr.Server.exe")
)
$exe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $exe) {
  throw "RevitVr.Server.exe not found. Run build.ps1 in C:\Users\micha\RevitVR first."
}
$exe = (Resolve-Path $exe).Path

$webCandidates = @(
  (Join-Path $addinDir "web"),
  (Join-Path $addinDir "vr-web"),
  (Join-Path $PSScriptRoot "..\dist\web"),
  (Join-Path $PSScriptRoot "..\web")
)
$web = $webCandidates | Where-Object { Test-Path (Join-Path $_ "index.html") } | Select-Object -First 1
if (-not $web) {
  throw "the web app (library homepage + viewer) was not found. Run build.ps1 first."
}
$web = (Resolve-Path $web).Path

New-Item -ItemType Directory -Force -Path $library, $scene | Out-Null

# ---- optional: a desktop / Start menu shortcut ---------------------------------
# Double-clicking it starts the server and opens the page, with no CAD app involved.
if ($InstallShortcut) {
  $shell = New-Object -ComObject WScript.Shell
  $powershellExe = Join-Path $env:SystemRoot "System32/WindowsPowerShell/v1.0/powershell.exe"
  $arguments = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$PSCommandPath`""
  $iconCandidate = Join-Path $PSScriptRoot "../web/favicon.ico"
  $icon = if (Test-Path $iconCandidate) { (Resolve-Path $iconCandidate).Path } else { $null }
  $places = @(
    [Environment]::GetFolderPath("Desktop"),
    (Join-Path $env:APPDATA "Microsoft/Windows/Start Menu/Programs")
  )
  foreach ($place in ($places | Where-Object { $_ } | Select-Object -Unique)) {
    New-Item -ItemType Directory -Force -Path $place | Out-Null
    $lnk = $shell.CreateShortcut((Join-Path $place "Model Library.lnk"))
    $lnk.TargetPath = $powershellExe
    $lnk.Arguments = $arguments
    $lnk.WorkingDirectory = $PSScriptRoot
    $lnk.Description = "Start the VR model library and open it in the browser"
    if ($icon) { $lnk.IconLocation = "$icon,0" }
    $lnk.Save()
    Write-Host "shortcut -> $(Join-Path $place 'Model Library.lnk')"
  }
}

# ---- is it already up? -------------------------------------------------------
function Test-LibraryServer([int]$p) {
  try {
    $s = Invoke-RestMethod "http://127.0.0.1:$p/api/status" -TimeoutSec 1
    return ($s.ok -eq $true -and $null -ne $s.library)
  } catch { return $false }
}

function Test-PortOpen([int]$p) {
  try {
    $c = New-Object System.Net.Sockets.TcpClient
    $c.Connect("127.0.0.1", $p)
    $c.Close()
    return $true
  } catch { return $false }
}

function Get-FreePort([int]$start) {
  for ($p = $start; $p -lt $start + 25; $p++) {
    if (-not (Test-PortOpen $p)) { return $p }
  }
  throw "no free port near $start"
}

if ($Stop) {
  $stopped = 0
  Get-CimInstance Win32_Process -Filter "Name='RevitVr.Server.exe'" | ForEach-Object {
    if ($_.CommandLine -match "--library") {
      Stop-Process -Id $_.ProcessId -Force
      $stopped++
    }
  }
  # Wait for the socket to close, so an immediate restart keeps the same port instead of drifting
  # to the next one and leaving a stale bookmark on the headset.
  for ($i = 0; $i -lt 12 -and (Test-PortOpen $Port); $i++) { Start-Sleep -Milliseconds 250 }
  Write-Host "stopped $stopped library server(s)"
  return
}

$reused = Test-LibraryServer $Port
if (-not $reused) {
  # Same reason: a server that just exited can hold the port for a moment.
  for ($i = 0; $i -lt 8 -and (Test-PortOpen $Port); $i++) { Start-Sleep -Milliseconds 250 }
  if (Test-PortOpen $Port) { $Port = Get-FreePort ($Port + 1) }

  $argv = @("--app", $web, "--scene", $scene, "--library", $library, "--port", "$Port")
  # The server always listens on http (loopback, for adb and this PC) and https (LAN, for the
  # headset over Wi-Fi), so -Lan has nothing left to switch on.
  Start-Process -FilePath $exe -ArgumentList $argv -WindowStyle Hidden | Out-Null

  $deadline = (Get-Date).AddSeconds(20)
  while ((Get-Date) -lt $deadline -and -not (Test-LibraryServer $Port)) {
    Start-Sleep -Milliseconds 250
  }
  if (-not (Test-LibraryServer $Port)) {
    throw "the server did not answer on port $Port - run it in a console to see why: `"$exe`" $($argv -join ' ')"
  }
}

$status = Invoke-RestMethod "http://127.0.0.1:$Port/api/status" -TimeoutSec 3
$models = [int]$status.models
$httpsPort = [int]$status.httpsPort
$url = "http://localhost:$Port/"
$label = if ($reused) { "already running" } else { "started" }

function Get-LanIp {
  # The first IPv4 that is not loopback or a VPN adapter: NordVPN's tunnels are what make a naive
  # "first address" pick something the headset cannot reach.
  $all = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -notmatch '^(127\.|169\.254\.)' -and $_.InterfaceAlias -notmatch 'VPN|NordLynx|TAP|Loopback' }
  $pref = $all | Where-Object { $_.IPAddress -like '192.168.*' } | Select-Object -First 1
  if (-not $pref) { $pref = $all | Select-Object -First 1 }
  if ($pref) { return $pref.IPAddress }
  return $null
}

Write-Host "model library $label - $models model(s)"
Write-Host "  this PC  $url"
if ($httpsPort -gt 0) {
  $ip = Get-LanIp
  if ($ip) { Write-Host "  headset  https://${ip}:$httpsPort/   (accept the certificate once)" }
} else {
  Write-Warning "  https listener did not start; the headset can only use the adb path"
}
Write-Host "  library  $library"

# ---- open it ----------------------------------------------------------------
if (-not $NoBrowser) { Start-Process $url | Out-Null }

if ($Headset) {
  $adbCandidates = @(
    (Get-Command adb -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
    (Join-Path $localAppData "Android\Sdk\platform-tools\adb.exe")
  )
  $adb = $adbCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
  $serial = $null
  if ($adb) {
    $serial = (& $adb devices) | Select-String "device$" | ForEach-Object { ($_ -split "\s+")[0] } | Select-Object -First 1
  }
  if ($serial) {
    & $adb -s $serial reverse "tcp:$Port" "tcp:$Port" | Out-Null
    & $adb -s $serial shell am start -a android.intent.action.VIEW -d $url | Out-Null
    Write-Host "opened on $serial (adb)"
  } elseif ($httpsPort -gt 0) {
    $ip = Get-LanIp
    if ($ip) {
      Write-Host "no headset over adb - open https://${ip}:$httpsPort/ in the Quest browser (accept the certificate once)"
    } else {
      Write-Warning "no headset over adb and no LAN address found"
    }
  } else {
    Write-Warning "no headset over adb, and the https listener is not up - nothing to open on the headset"
  }
}
