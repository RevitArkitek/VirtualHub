# RevitVR

One button in a Revit 3D view. Put on the Quest. Walk the building. Walls stop you, doorways
don't.

This is a working implementation of the Enscape *workflow* — click in Revit, be inside your model
seconds later — built without a game engine, without a licence, and without sideloading an APK.
It is not a reimplementation of Enscape's renderer; see [How this differs](#how-this-differs-from-enscape).

```
   Revit (C#, .NET 8)                    PC                          Quest 3
┌────────────────────┐   ┌──────────────────────────┐   ┌────────────────────────┐
│ Enter VR button    │   │ scene.glb  (merged mesh)  │   │ Quest Browser          │
│ ├ model walk (instances) ─▶ colliders.json (2D boxes)│──▶│ ├ three.js + WebXR     │
│ └ wall/door harvest│   │ local server :8765        │adb│ ├ capsule vs walls     │
└────────────────────┘   └──────────────────────────┘rev│ └ floor snapping       │
                                                        └────────────────────────┘
```

## Status

Built and verified on this machine (Revit 2025, .NET 8, Quest path via adb):

| Piece | State |
|---|---|
| Revit add-in (ribbon, export, collider harvest) | compiles against real `RevitAPI.dll` / `RevitAPIUI.dll` |
| GLB writer | passes the official Khronos `gltf-validator` — 0 errors, 0 warnings |
| Door cut-out geometry | 19/19 assertions on the shipped code (straight, curved and overlapping cases) |
| Collision engine | 11/11 headless assertions incl. "walls block, doorways pass" |
| WebXR viewer | loads, renders, walks, collides in a real browser |
| Local server | serves viewer + scene, HTTP loopback and HTTPS LAN |
| Installer | `dist\` assembled, installed to Revit's addins folder, deployment re-checked |
| **Export of an actual building** | **not yet run — needs a Revit session and your model** |

The one thing not exercised end-to-end is harvesting geometry from a live Revit document. That
needs Revit open with a model and a licensed session, which cannot be automated here. Everything
downstream of it is tested.

## Install

Requires Windows, Revit 2025, a Quest 3, and `adb` (Android platform-tools).

```powershell
.\build.ps1                 # builds everything, installs into Revit 2025
.\build.ps1 -RevitYear 2026 # retarget (any Revit whose API is .NET 8: 2025+)
.\build.ps1 -NoInstall      # just assemble dist\
```

Then:

1. **Quest**: Settings → Developer Mode on (one-time, via the Meta Horizon app). Connect USB and
   accept the *Allow USB debugging* prompt. Wireless debugging works too.
2. **Revit**: open a project, open a 3D view, **Add-Ins → RevitVR → Enter VR**.
3. Put the headset on and press **Enter VR** in the browser page that opened.

The view's crop box and visibility settings decide what gets exported — crop to the floor you
want to walk, or use a section box.

No adb? The add-in falls back to HTTPS over Wi-Fi and shows you the address. Accept the
self-signed certificate warning once on the headset.

## Controls

| | |
|---|---|
| Left stick | walk |
| Right stick | snap turn (30°) |
| Trigger / A | toggle fly mode |
| Desktop | WASD, drag to look, `F` fly, `R` respawn |

## Test it without Revit

The viewer is decoupled from Revit on purpose, so the whole runtime can be exercised with a
synthetic scene:

```powershell
dotnet run --project tools/demogen -- runtime/export      # two rooms, one doorway
dotnet run --project src/RevitVr.Server -- --app vr-web --scene runtime/export --port 8765
```

Open <http://127.0.0.1:8765/> and walk into the doorway.

## Verification

Each of these is a real check, not a smoke test:

```powershell
# the GLB the exporter produces is spec-valid
node tools/gltfcheck/validate.mjs runtime/export/scene.glb

# door cut-outs land in the right place on the wall (the code the add-in actually runs)
dotnet run --project tools/test-wallcut

# walls block, doorways pass, corners hold, storeys don't interfere
node tools/test-collision.mjs runtime/export/colliders.json

# the installed add-in resolves: class names, interfaces, and every asset path Revit will need
dotnet run --project tools/addincheck
```

## How this differs from Enscape

Enscape is a real-time renderer: path-traced global illumination, reflections, a full material
system, vegetation, animation, live bi-directional sync. It is a proprietary engine with a large
team behind it.

This reproduces the *interaction model*, not the pixels:

- **Click → be inside the model.** Same.
- **Walls are solid, doors are passable.** Same, and solved more cheaply (below).
- **Rendering.** Base-colour + roughness per material, one directional light with a frozen shadow
  map, no GI, no reflections, no textures yet. A clean schematic look rather than a photoreal one.

## Design decisions worth knowing

**WebXR in the headset's own browser, not a Unity/OpenXR APK.** A native build means a second
engine, a build pipeline, and a sideload for every iteration. The Quest 3 runs WebXR at 72 Hz
with a browser the user already has, so the inner loop is "save file, refresh". The cost is GPU
headroom and no baked lighting, which is the right trade for a design-review tool.

**No certificate prompt, by construction.** WebXR requires a secure context, so plain
`http://192.168.x.x` is refused by the browser. Rather than fight self-signed certs, the add-in
runs `adb reverse tcp:8765 tcp:8765`, which makes the PC's server appear on the *headset's*
`localhost`. Browsers treat `localhost` as trustworthy, so WebXR is allowed with no certificate
at all. That is also what makes the flow genuinely one click.

**Harvest geometry by walking the model, not with `CustomExporter`.** The obvious approach is
`CustomExporter` + `IExportContext`, and it is wrong for this job: it delivers family-instance
geometry in *symbol* space and exposes no transform on `InstanceNode` (or any other render node).
Every door, window, mullion and chair then lands at the origin, stacked on itself — the building
looks right because walls and floors are not instances, but everything you can walk through or
look at is missing. Walking the document instead gives `GeometryInstance.Transform`, which is the
only thing that places instances correctly, and lets the export force `DetailLevel.Fine` so
curtain wall panels and mullions are generated regardless of the source view's settings.

**Analytic colliders, not mesh collision.** A Revit model is hundreds of thousands of triangles;
sweeping a capsule against that per frame will not hold 72 fps on a standalone headset. So the
add-in derives collision from the *model data instead of the render mesh*:

- every wall becomes a 2D box along its location curve (arcs are tessellated), carrying its
  height range so storeys cannot interfere with each other;
- every door cuts a gap out of the wall run it sits in, using `DOOR_WIDTH` and the door's
  projection onto the wall's curve. A door hosted to a curtain wall is matched either through
  `FamilyInstance.Host` or geometrically, and every run it sits on is cut;
- floor slabs **and roofs** become walkable rectangles used for ground snapping within one stair
  riser. A pitched roof is treated as a flat plane at its highest point, so only flat roofs are
  exact;
- **stair treads** are taken from the upward-facing planar faces of the stair geometry, each at
  its own height and as a polygon rather than a bounding box — a flight on the diagonal would
  otherwise become a walkable slab spanning far wider than the stair;
- **topography** is emitted as triangles, and the viewer interpolates height across each one. A
  hillside has no single height, so it cannot use the one-height-per-surface model that slabs
  and treads use;
- **casework, furniture, entourage, columns and plumbing fixtures** get obstacle colliders so you
  cannot walk through them. Each instance's footprint is fitted with a minimum-area rectangle
  rather than a world-axis box — a desk rotated 45 degrees would otherwise block roughly twice
  its own area — and emitted as the same oriented box walls use, so the viewer needs no extra
  collision path. Doors are deliberately excluded: the wall gets a gap instead, and the leaf
  stays passable;
- a light fixture's photometric web — the mesh that shows how light spreads — is skipped, since
  it is a modelling aid rather than building fabric.

The result is a few hundred cheap 2D tests behind a spatial hash, and it is geometrically exact
because it comes from the same curves the walls are built from — not from a decimated mesh.

**Merged geometry per material.** Revit's exporter emits per-element polymorph meshes; a building
has thousands. Everything is merged into one primitive per material, so the draw call count equals
the number of materials rather than the number of elements.

## What is not built yet

Roughly in order of how much they'd improve it:

- **Textures.** Only base colour and roughness are exported. Pulling texture maps out of Revit
  appearance assets is the single biggest visual upgrade.
- **Large-model handling.** No LOD, no occlusion culling, no streaming, no compression. A
  1,000,000-triangle view will produce a ~100 MB GLB. Next step: per-element culling by room and
  `meshopt` compression.
- **Ramps.** Stair treads are walkable because each tread is a horizontal plane. A ramped
  surface is one sloped face, so it is skipped rather than approximated by a plane at its top —
  walking a ramp still needs fly mode.
- **Live sync.** Enscape-style auto-refresh on model change is not implemented; re-click to
  re-export. The export is also synchronous on Revit's UI thread, so a big model blocks Revit
  while it runs (an `ExternalEvent` + progress bar is the fix).
- **Overrides.** No way to mark a wall as passable, no swappable furniture, no measured
  walkthrough, no annotation.
- **Hands / teleport.** Controllers are ray lines only; no arc teleport, no hand tracking.

## Layout

```
src/RevitVr.Core/        GLB writer + scene schema (no Revit dependency, unit-testable)
src/RevitVr.Addin/       Revit add-in: ribbon, IPhotoRenderContext export, collider harvest, adb
src/RevitVr.Server/      Kestrel static server: viewer at /, scene at /scene/, /api/status
vr-web/                  WebXR viewer: three.js, locomotion, collision
tools/demogen/           synthetic two-room scene in the export format
tools/test-collision.mjs acceptance test for the collision rules
tools/gltfcheck/         Khronos gltf-validator wrapper
tools/addincheck/        verifies the deployed add-in resolves
tools/mkicon/            draws the ribbon icons
```

## Troubleshooting

**Button missing.** Restart Revit. Check the manifest parses:
`%APPDATA%\Autodesk\Revit\Addins\2025\RevitVr.addin` — the `<Assembly>` path must be a real file.
`dotnet run --project tools/addincheck` tells you exactly which piece is missing.

**"Revit cannot run the external application" on startup.** The add-in failed in `OnStartup`;
it now logs the exception to `%LOCALAPPDATA%\RevitVR\addin.log` and shows a dialog saying so.
Send that file.

**"No headset connected over adb."** `adb devices` must list your Quest as `device`, not
`unauthorized`. If unauthorized, put the headset on and accept the prompt.

**Page loads but Enter VR is disabled.** The browser has no XR device — you are on the desktop,
or the origin is not a secure context. On the headset use the `localhost` address (via adb
reverse) or the HTTPS address.

**Exported nothing.** The view was empty or fully cropped away.
