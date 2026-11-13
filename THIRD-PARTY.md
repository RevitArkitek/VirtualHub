# Third-party components

VirtualHub itself is GPL-3.0-or-later (see LICENSE). It depends on, vendors, or talks to the
following. Nothing here is modified except where noted.

## Vendored in this repository

| Component | Where | Licence |
|---|---|---|
| three.js (including GLTFLoader and BufferGeometryUtils) | `web/vendor/` | MIT |
| glTF-Validator (`gltf-validator` npm package, dev only) | `tools/gltfcheck/` | Apache-2.0 |

The three.js files carry their own licence header and are unmodified. The glTF validator is
installed by `npm ci` and is not committed.

## Required to build or run

| Component | Licence | Redistributed? |
|---|---|---|
| .NET / ASP.NET Core | MIT | no — installed by the user or self-contained in a published server |
| Windows PowerShell | MIT | no |
| Node.js | MIT | no |
| Python | PSF-2.0 | no |

## Host applications (not included)

| Application | Licence | Relationship |
|---|---|---|
| Autodesk Revit | proprietary | the add-in references `RevitAPI.dll` from a local installation. **No Autodesk binary or assembly is distributed with this project.** |
| IngeTrazo | GPL-3.0 | the plugin is loaded by IngeTrazo at runtime and uses its Python API. IngeTrazo itself is not bundled. |

## Trademarks

Autodesk, Revit, IngeTrazo and Meta Quest are trademarks of their respective owners. This project is
not affiliated with or endorsed by any of them.
