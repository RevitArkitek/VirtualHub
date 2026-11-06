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
| Textures (appearance-asset images, UVs, normal maps) | 46/46 headless assertions; textured GLB validates with 0 errors, 0 warnings and renders in a real browser |
| Baked ambient occlusion (BVH raycast → `COLOR_0`) | 18/18 headless assertions; visible in the viewer at wall bases, corners and doorways |
| Door cut-out geometry | 19/19 assertions on the shipped code (straight, curved and overlapping cases) |
| Collider geometry (fitting, railing paths, containment) | 48/48 (rectangles, circles, degenerate input, measured heights, dropped phantom pieces, resampling, point-in-solid) |
| Collision engine | 29/29 headless assertions — walls, doorways, stairs, terrain, oriented obstacles, railings |
| WebXR viewer | loads, renders, walks, climbs, collides in a real browser |
| Local server | serves viewer + scene, HTTP loopback and HTTPS LAN; certificate persisted |
| Installer | `dist\` assembled, installed to Revit's addins folder, deployment re-checked |
| **Export of an actual building** | **not yet run — needs a Revit session and your model** |

The one thing not exercised end-to-end is harvesting geometry from a live Revit document. That
needs Revit open with a model and a licensed session, which cannot be automated here. Everything
downstream of it is tested.

### Where this stands

Verified in-session against the demo scene and a real browser: instance placement, wall and door
colliders, stair treads, terrain interpolation, and furniture obstacles.

**Awaiting one Revit export to confirm against a real model** — the numbers to check are printed
in the export dialog, and each has a documented meaning. The texture harvest is the newest and
least-proven piece: its parts are tested on their own (image preparation, path resolution, UVs,
the GLB), but which materials in *your* model resolve an appearance image can only be seen by
exporting it. The dialog names any texture it could not find, and those paths are the one thing
likely to need extending in `TexturePathResolver.DefaultRoots()`.

| Report line | What it tells you |
|---|---|
| `elements, instances, solids` | `instances` near zero means family instances aren't being reached |
| `textures: N embedded (X MB), M of K materials textured` | `M` zero means no material's appearance asset yielded an image — every surface will be flat colour |
| `ambient occlusion: N vertices, R rays, A → B tris, T s` | `A → B` is the subdivision the bake needed; a big jump with `T` over a few seconds means the export was already heavy. The line reads `off (settings.ini)` when baking is disabled |
| `texture scale: median X m per repeat (range A–B m)` | one repeat of the image per X metres of building — if this is tens of metres, a texture's unit was misread |
| `wall runs → N wall segments, M obstacle boxes` | `M` zero means the obstacle categories aren't matching your families |
| `railings: N found → M colliders (P height-measured, Q path-elevation, F footprint)` | `Q > 0` means those railings had no geometry to measure, so their barrier sits at the level's elevation and may be a storey off; `M` zero means railings exist but none produced a barrier |
| `doors: N found, M cut` | `M < N` means some door hosts aren't resolving |
| `surfaces, treads, terrain triangles` | zero treads/terrain means the geometry didn't produce walkable faces |
| `light-source meshes skipped` | absent means the name heuristic missed your fixture families |
| `N railings lost path pieces` | those railings have gaps in their barrier — `railings.json` names them by ElementId |

### Operational gotchas

- **`%LOCALAPPDATA%\RevitVR\settings.ini`** decides whether the export bakes occlusion (off by
  default), how far it subdivides, and how many rays it casts. It is written on the first export
  with comments explaining each option; edit it and export again, no restart needed.
- **Close Revit before running `build.ps1`.** It holds `RevitVr.Addin.dll` open and the install
  will fail partway. The script does not force-kill Revit, by design.
- **Windows Firewall** needs an inbound rule for `RevitVr.Server.exe` or the headset cannot reach
  the HTTPS address. `build.ps1` adds it when run as Administrator.
- **The LAN certificate is persisted** at `%LOCALAPPDATA%\RevitVR\cert.pfx`, so the headset only
  accepts the warning once. Regenerate with `--new-cert`.
- **`adb devices` must show the headset** for the one-click path. Link and casting are not adb.


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
| Right stick left/right | snap turn 30° per push, in the direction you push — let the stick spring back before the next step |
| Trigger / A | toggle fly mode |
| Left trigger (hold) | fast navigation: 3× movement speed while held — walking, flying and climbing; snap turn stays 30° |
| While flying | left stick ↑↓ forward/back, ←→ strafe — same as walking. Right stick ↑↓ climbs (push forward) / descends (pull back), ←→ still snap turns |
| Desktop | WASD, drag to look, `F` fly, `Q`/`E` down/up, `R` respawn |

## Test it without Revit

The viewer is decoupled from Revit on purpose, so the whole runtime can be exercised with a
synthetic scene:

```powershell
dotnet run --project tools/demogen -- runtime/export      # two rooms, a doorway, a stair and its guard rail
dotnet run --project src/RevitVr.Server -- --app vr-web --scene runtime/export --port 8765
```

Open <http://127.0.0.1:8765/> and walk into the doorway. One room is closed, with a ceiling and a
window — useful for judging lighting, and it is the room the lightmap spike was measured in
(`--bake-preview` bakes the spike's indirect light into the same scene).

## Verification

Each of these is a real check, not a smoke test:

```powershell
# the GLB the exporter produces is spec-valid
node tools/gltfcheck/validate.mjs runtime/export/scene.glb

# door cut-outs land in the right place on the wall (the code the add-in actually runs)
dotnet run --project tools/test-wallcut

# footprint fitting and railing path boxes: the arithmetic behind every collider
dotnet run --project tools/test-planrect

# walls block, doorways pass, corners hold, storeys don't interfere, railings hold the edge
node tools/test-collision.mjs runtime/export/colliders.json

# the installed add-in resolves: class names, interfaces, and every asset path Revit will need
dotnet run --project tools/addincheck

# railing colliders are railing-sized and agree with what renders (one storey off, or four metres tall)
node tools/railcheck.mjs runtime/export

# image preparation, texture-path resolution, UV projection and the textured GLB
dotnet run --project tools/test-textures

# ray casting, occlusion falloff, subdivision and the COLOR_0 the bake writes
dotnet run --project tools/test-ao
```

## How this differs from Enscape

Enscape is a real-time renderer: path-traced global illumination, reflections, a full material
system, vegetation, animation, live bi-directional sync. It is a proprietary engine with a large
team behind it.

This reproduces the *interaction model*, not the pixels:

- **Click → be inside the model.** Same.
- **Walls are solid, doors are passable.** Same, and solved more cheaply (below).
- **Materials.** Base colour or texture, roughness, normal map per material — taken from the
  render appearance, at the real-world size it was authored at.
- **Rendering.** One directional light with a frozen shadow map, a procedural environment probe
  and baked ambient occlusion: no global illumination, no reflections, no baked light. A clean,
  evenly lit look rather than a photoreal one — surfaces have texture and their corners are shaded,
  but an unlit corner stays as bright as a lit one.

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
- **railings** become thin boxes along `Railing.GetPath()`, at the height their geometry actually
  occupies rather than the path's own Z. That distinction is the whole bug this feature had:
  `GetPath()` reports the path at the *level's* elevation, so a railing on an upper floor arrives
  flat at that floor's Z, and a barrier raised from it sits one storey below the railing — you
  walk through the railing upstairs and are stopped by nothing downstairs, in open space. Each
  path point is therefore measured against the railing's own geometry for its bottom and top,
  along the path every 600 mm rather than only at its vertices — a straight path arrives as two
  points, and measuring those alone spans a whole stair flight and yields a four-metre-tall box.
  The measured span is clamped to the railing type's own height, because the measuring disc is a
  plan disc and can otherwise catch whatever is stacked above the railing. Path pieces that run
  past the visible railing are dropped rather than left as colliders in open space. Measuring
  asks *which solids span this point*, not which bounding-box corners are near it: a 4 m rail's
  corners sit only at its ends, so a corner test leaves holes between posts — and a hole is a
  railing you walk through. A railing whose geometry cannot be read at all is emitted at the path
  elevation and counted in the export report, because that is the case that hides a railing.
  Every railing collider carries its `sourceId`, and `railings.json` beside the scene records what
  each railing element produced (`measured` / `path-elevation` / `footprint` / `none`), how many
  path samples found no geometry, and its height — so a railing that misbehaves in the headset can
  be traced to an ElementId instead of guessed at from coordinates;
- a light fixture's photometric web — the mesh that shows how light spreads — is skipped, since
  it is a modelling aid rather than building fabric.

The result is a few hundred cheap 2D tests behind a spatial hash, and it is geometrically exact
because it comes from the same curves the walls are built from — not from a decimated mesh.

**Merged geometry per material.** Revit's exporter emits per-element polymorph meshes; a building
has thousands. Everything is merged into one primitive per material, so the draw call count equals
the number of materials rather than the number of elements.

**Textures come from the render appearance, not the shading colour.** A material's appearance
asset, three levels down (`Material.AppearanceAssetId` → `AppearanceAssetElement` →
`Asset`), carries an image only sometimes: a plain "Generic" material has a diffuse colour, and a
procedural asset (wood, marble, metal) has parameters and no image at all. So the exporter asks
each material what it has and takes what it finds — base-colour map, bump map as a normal map,
diffuse colour, metal flag — and falls back to the shading colour for anything left over.

Three things make the images usable rather than merely present:

- **Revit geometry has no UVs.** None. Every tessellated face is positions and normals, so UVs are
  generated during export by box projection in world space, decided per vertex by the normal's
  dominant axis. Walls, floors and ceilings — what a walkthrough actually converges on — are each
  a single plane, so the projection is exact for them, and a wall seen from its far side is not
  mirrored. Curved surfaces get a seam where the dominant axis flips, which is the standard trade.
- **Real-world scale, in whatever unit Revit used.** Revit stores a "Size X/Y" per texture — how
  much building one repeat of the image covers — but the value is a bare double whose unit varies
  with the document (`AssetPropertyDistance`: *"it holds a double value; the unit varies"*), so it
  is converted using the unit the property itself reports. Assuming feet, as the first version did,
  turns a 300 mm metric tile into 91 m and smears one image across an entire wall. A size outside
  anything a texture can be is rejected rather than used, and named in the export report.
- **Images are turned into something a GLB can carry.** glTF allows PNG and JPEG only, so a TiFF
  or BMP texture is transcoded; anything over 2048 px on its longest side is scaled down (a Revit
  library is full of 4K scans, and twenty of those will not fit in a standalone headset); opaque
  PNGs become JPEG, since most are photographs stored in the wrong container. Any file that is
  already a small PNG/JPEG is embedded byte for byte. Identical images are stored once, and every
  material sharing one library bitmap points at a single copy.

A texture that cannot be found is named in the export dialog rather than silently ignored — the
difference between "textures do nothing" and "this file moved" is otherwise invisible. The same
goes for scale: `textures.json` beside the scene records, per material, the raw value Revit
reported, the unit it claimed, and the tile size that went into the GLB, because "the textures are
the wrong size" is a question that deserves numbers rather than a guess.

**Occlusion is baked, not computed.** The scene is static, so ambient occlusion is raycast once on
the PC and written into the GLB as per-vertex colour (`COLOR_0`), which every glTF renderer
multiplies into base colour for free: four bytes per vertex, nothing per frame, no second texture,
no lightmap UVs. A BVH over the merged triangles answers "does anything block this ray before
0.8 m?" for 48 cosine-weighted directions per vertex, split across all cores.

It is **off by default**, because it changes the mesh: it subdivides large triangles so the shading
has somewhere to land, which is visible geometry if you are checking your model rather than walking
it. Turn it on in `%LOCALAPPDATA%\RevitVR\settings.ini` (written on first export, with comments),
where the subdivision size and ray count live too.

Two details make it look like shadowing rather than dirt:

- **Geometry is subdivided first.** A Revit floor slab is two triangles, and interpolating four
  corner values across a room is a vignette, not contact shading. Triangles longer than 0.6 m are
  split at their longest edge (welding shared corners back together) until the shading has
  somewhere to land — bounded by a triangle budget that scales with the model.
- **The result is smoothed** over the vertex graph. A finite ray count is noisy per vertex, and
  noise interpolated across a triangle shows up as visible facets on a flat wall — which is exactly
  how the first version looked. Eight passes at 48 rays leave the gradient and remove the noise.

That smoothing is a trade, not a free win: it also softens the contact darkening, because the
signal and the noise live at the same scale. The defaults lean toward artifact-free, since a wall
covered in visible triangles reads as broken in a way that slightly softer corner shading does not.

**It is off by default for a reason.** On a real building — large flat walls, few vertices per
face — the bake was reported back as "triangles on the walls", and it made the export noticeably
slower. Both are inherent to shading a big plane from its vertices: more subdivision means more
triangles, less subdivision means more banding, and either way it is a mesh change. The fix is not
a better tuning of this bake but storing light in a lightmap, which is the work the spike scoped.

What it cannot do is add light: there is no bounce, no colour bleeding and no light from windows,
so a corner darkened by occlusion is still not lit the way it would be in a real room. That is the
next tier, and it needs lightmaps, not vertices.

## What is not built yet

Roughly in order of how much they'd improve it:

- **Lighting.** There is baked occlusion now, but no global illumination and no reflections — which
  is still the largest gap to Enscape: a corner is darkened correctly but never *brightened* by
  light bouncing off a wall or coming through a window.
- **Material fidelity.** Only the diffuse/bump maps are read. Transparency, self-illumination,
  refraction and Revit's procedural wood/marble/metal assets all fall back to flat colour.
- **Texture alignment.** Box projection ignores the per-material rotation and mapping transform,
  so a texture rotated in Revit arrives axis-aligned; UVs are not atlased, so each texture binds
  separately.
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

## Spikes

`spikes/` holds throwaway experiments that decided what to build next.

**001 — baked indirect light.** Baked sky visibility plus three diffuse bounces per vertex to find
out whether global illumination is what the look is missing, without building an unwrapper first.
Verdict: the *lighting* is the missing piece, the *storage* is not. Per-vertex colour multiplies
direct sun as well as ambient, so it cannot carry baked light without either double-darkening the
sun or switching the sun off; it is also too coarse to hold a window's beam, and it seams where
elements' vertices do not align. The real build is a lightmap in three's `lightMap` slot — which is
indirect-only and therefore composes with the real-time sun — with a `TEXCOORD_1` atlas, a sidecar
beside the scene, and a budget of about a minute per room at 1024² from the measured throughput.
Details, numbers and the recommendation are in
[`spikes/001-baked-indirect/README.md`](spikes/001-baked-indirect/README.md); the shipped default is
still occlusion only.

## Layout

```
src/RevitVr.Core/        GLB writer + scene schema (no Revit dependency, unit-testable)
src/RevitVr.Addin/       Revit add-in: ribbon, IPhotoRenderContext export, collider harvest, adb
src/RevitVr.Server/      Kestrel static server: viewer at /, scene at /scene/, /api/status
vr-web/                  WebXR viewer: three.js, locomotion, collision
tools/demogen/           synthetic two-room scene in the export format, textures included
tools/test-collision.mjs acceptance test for the collision rules
tools/test-textures/     texture pipeline: encoder, path resolver, UVs, textured GLB
tools/test-ao/           occlusion bake: BVH, falloff, subdivision, COLOR_0
tools/railcheck.mjs     railing colliders vs the geometry that actually renders
tools/gltfcheck/         Khronos gltf-validator wrapper
tools/addincheck/        verifies the deployed add-in resolves
tools/mkicon/            draws the ribbon icons
spikes/                  throwaway experiments that decided what to build next
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
