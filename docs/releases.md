# Releases

GitHub attaches the repository's source archives automatically. Everything else is a built
asset — and for a project with a proprietary SDK in the loop, which assets exist is not obvious.

```powershell
.\plugins\revit\build.ps1 -NoInstall          # the add-in (needs Revit installed)
.\tools\package-release.ps1 -Version v0.1.0 -WithRevit
```

That writes `release\v0.1.0\` with the zips and `SHA256SUMS.txt`. Attach the zips and the checksum
file to the release. CI builds the first two on every tag; the Revit one is attached by hand.

## The three assets

| Asset | Size | Contains | For |
|---|---|---|---|
| `virtualhub-revit-<year>-<version>.zip` | ~42 MB | `install.ps1`, the add-in (`RevitVr.Addin.dll`, `RevitVr.Core.dll`, `Resources/`), the self-contained `RevitVr.Server.exe`, the whole `web/` app | Revit users |
| `virtualhub-ingetrazo-<version>.zip` | ~0.3 MB | `plugins/ingetrazo/{ingetrazo_vr.py, install.ps1}` and `web/` | IngeTrazo users |
| `virtualhub-server-<version>.zip` | ~42 MB | `server/RevitVr.Server.exe`, `web/`, `start-library.ps1` | browsing and walking the library with no CAD app |

Each zip contains one folder with a `README.txt` and an installer or launcher, so it works after
unzipping anywhere — nothing is pre-wired to a path on the build machine.

**One Revit package per Revit year.** The add-in is compiled against a specific `RevitAPI.dll`
(`-RevitYear 2025` / `2026`); a 2025 build will not load in Revit 2026. Publish
`virtualhub-revit-2025-…` and, when you have built against it, `virtualhub-revit-2026-…`.

The IngeTrazo package is small because the server is not in it: that plugin runs its own embedded
Python server, which is why IngeTrazo users need no .NET at all.

## What must never be attached

- **`dist\` as-is.** Its `RevitVr.addin` contains the absolute path of the build machine's
  `RevitVr.Addin.dll`. The packaged `install.ps1` writes that manifest for the user's own install
  path instead.
- **Autodesk assemblies** (`RevitAPI.dll`, `RevitAPIUI.dll`, `AdWindows.dll`). Never redistribute
  them; the add-in references the user's installed Revit.
- **Certificates or keys** (`cert.pfx`, `cert.pem`, `key.pem`). Each machine generates its own.
- **`.pdb`, `web.config`, `obj/`, `bin/`, `node_modules/`.** Debug and hosting leftovers.
- **Exported models or client screenshots.** The library lives in `%LOCALAPPDATA%\VRHub\library`
  on the user's machine, not in a release.

## Verifying a package

```powershell
Get-FileHash .\virtualhub-revit-2025-v0.1.0.zip -Algorithm SHA256   # compare with SHA256SUMS.txt
```

Then unzip and run its `install.ps1` (or `start-library.ps1`) from the unzipped folder. The release
workflow's packages are built by the same script you run locally, so what you test is what ships.

## Version numbers

Tags are the version. `v0.1.0` is the first public tag; the library layout and the ports are the
stable parts, the IngeTrazo plugin API target is not (IngeTrazo is 0.x). When the plugin's
compatibility changes, say which IngeTrazo version it was tested against in the release notes.
