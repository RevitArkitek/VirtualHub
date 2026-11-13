# Contributing

Thanks for looking. This is a small project with a wide test surface, and most of the value is in
the parts that can be checked without owning the CAD software.

## Prerequisites

| Need | For | Notes |
|---|---|---|
| Windows 10/11 | everything | both plugins are Windows-only by nature |
| .NET SDK 10 | server, tools, Revit add-in | Revit 2025.5+/2026 run on .NET 10; a user-local SDK in `%USERPROFILE%\.dotnet` is picked up automatically |
| Node.js 20+ | collision tests, glTF validation | |
| Python 3.10+ | IngeTrazo plugin and its tests | the plugin itself has no third-party dependencies |
| Revit 2025/2026 | building and running the Revit add-in | Autodesk's DLLs cannot be shipped or downloaded here |
| IngeTrazo 0.3.x | running a real IngeTrazo export | the plugin talks to its host API, which is still 0.x |

You can develop and test everything except the two plugins' UI without either CAD application.

## Build and test

```powershell
# server + the Revit-free tool set
dotnet build server/RevitVr.Server/RevitVr.Server.csproj -c Release
foreach ($p in 'tools/test-wallcut','tools/test-planrect','tools/test-ao','tools/test-textures','tools/demogen') {
  dotnet build $p -c Release
}

# assertions
dotnet run --project tools/test-wallcut -c Release --no-build
dotnet run --project tools/test-planrect -c Release --no-build
dotnet run --project tools/test-ao -c Release --no-build
dotnet run --project tools/test-textures -c Release --no-build

# a synthetic scene, then collision + glTF validity
dotnet run --project tools/demogen -c Release --no-build -- runtime/export
node tools/test-collision.mjs runtime/export/colliders.json
npm --prefix tools/gltfcheck ci
node tools/gltfcheck/validate.mjs runtime/export/scene.glb

# the Python plugin (no IngeTrazo needed)
python tools/ingetrazo/test_derive.py
python tools/ingetrazo/test_library.py

# both servers against one library — the most useful test when touching routing
dotnet publish server/RevitVr.Server/RevitVr.Server.csproj -c Release -r win-x64 --self-contained true -o dist/server
python tools/conformance.py
```

The Revit add-in needs a Revit installation:

```powershell
.\plugins\revit\build.ps1                 # builds, installs, smoke-tests
.\plugins\revit\build.ps1 -NoInstall      # assemble dist\ only
dotnet run --project tools/addincheck     # what CI cannot do: check the deployed add-in
```

`addincheck` also runs the shared slug cases from `contracts/slug-cases.json` against the C#
implementation — the Python test runs the same file, which is how the two stay in agreement.

## What CI does and does not cover

CI runs on a hosted Windows runner: every .NET and Node and Python test above, plus the
conformance test. It **cannot** build the Revit add-in, because that needs `RevitAPI.dll`, which is
proprietary and must not be committed. Do not add it, or any Autodesk installer, to the repository.
Changes to the add-in should be checked locally with `build.ps1` and `addincheck`, and the pull
request should say what you ran.

## House rules

- **Comment why, not what.** The code explains comparisons, odd units and workarounds; a comment
  restating the line is noise.
- **A behaviour change needs a test.** Most of what has gone wrong here (railing height, door
  cut-outs, slug drift, traversal) is cheap to test and expensive to notice in a headset.
- **Both languages, one contract.** If you change how a slug is built, change
  `contracts/slug-cases.json` and both implementations, and run both sides.
- **Do not change runtime paths or ports casually.** `%LOCALAPPDATA%\VRHub\library`, the installed
  folder names and the port pairs are contracts with existing installations and headset bookmarks.
  If a rename is unavoidable, keep the old location working.
- **Vendored code** (`web/vendor/`) is three.js. Upgrading it is fine; mention the version and run
  the viewer against the demo scene afterwards.
- **Keep the ASCII rule for PowerShell.** Windows PowerShell 5.1 reads `.ps1` as ANSI without a
  BOM, so a UTF-8 em dash becomes a smart quote and breaks parsing. `.editorconfig` sets
  `utf-8-bom` for `*.ps1`; prefer plain ASCII in those files anyway.

## Pull requests

Small and focused beats large and tidy. Say what you changed, what you ran, and what you could not
run (for example "no Revit here, so the add-in change is untested"). Screenshots help for anything
visual; the synthetic demo scene is the right thing to photograph, not a client's model.

By contributing you agree that your work is licensed under the project's licence (GPL-3.0-or-later)
and to follow the [Code of Conduct](CODE_OF_CONDUCT.md).
