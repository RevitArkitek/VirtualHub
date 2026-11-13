# Architecture

Two CAD plugins, one runtime. The split exists because the hosts are different languages and
different apps, but everything downstream of "write a scene" is shared.

```
  Revit (C#)                        IngeTrazo (Python)
  ┌──────────────────────┐          ┌──────────────────────┐
  │ RevitVr.Addin        │          │ ingetrazo_vr.py      │
  │ export + colliders   │          │ export + colliders   │
  └─────────┬────────────┘          └─────────┬────────────┘
            │ publish                        │ publish
            ▼                                ▼
      ┌──────────────────────────────────────────────┐
      │ %LOCALAPPDATA%\VRHub\library\<slug>\         │
      │   meta.json   scene\scene.glb, colliders.json│
      └───────────────┬──────────────────────────────┘
                      │ served by either server
        ┌─────────────┴──────────────┐
        ▼                            ▼
  server/RevitVr.Server        embedded Python server
  (Kestrel, self-contained)    (in ingetrazo_vr.py)
        └─────────────┬──────────────┘
                      ▼
                 web/ (homepage + viewer)
                      ▼
             Quest browser, WebXR session
```

## Components

| Path | Language | Runs where | Responsibility |
|---|---|---|---|
| `plugins/revit/RevitVr.Addin` | C# | inside Revit | ribbon, geometry harvest, collider derivation, publish, adb launch |
| `plugins/revit/RevitVr.Core` | C# | anywhere | GLB writer, scene schema, occlusion bake, network helpers — no Revit dependency |
| `plugins/ingetrazo/ingetrazo_vr.py` | Python | inside IngeTrazo | export, collider derivation from geometry, publish, HTTPS/adb launch, its own server |
| `server/RevitVr.Server` | C# | standalone | serves the web app, the library, and one legacy scene; also `/api/models` |
| `web/` | JS/HTML | browser | model library homepage and the WebXR viewer (three.js vendored, no build step) |
| `contracts/` | JSON | tests | slug cases and metadata schema that both languages must satisfy |
| `tools/` | mixed | CI / maintainer | generators, probes and tests, including the conformance test |

## Why two servers

The Revit plugin can rely on .NET being installable; IngeTrazo users should not need it at all.
Shipping the Python server keeps `install.ps1` a file copy.

The cost is two implementations of the same routes, which drift silently — the headset then shows
different models, or a 404, depending on which plugin was opened last. `tools/conformance.py`
starts both against one fixture library and compares the model list, the file bytes, the static
pages and the guards. It has already caught one real divergence (encoded path traversal answered
200 by Kestrel, 404 by Python).

## Ports and transports

| Server | HTTP (loopback) | HTTPS (LAN) |
|---|---|---|
| Revit plugin's `RevitVr.Server` | `:8765` | `:8766` |
| IngeTrazo plugin's embedded server | `:8770` | `:8771` |

Both listen on both transports at once. Loopback HTTP is for this PC and for `adb reverse` (which
makes it appear as the headset's `localhost`, a secure origin); HTTPS is for the headset over
Wi-Fi, where a secure origin is mandatory. See [headset.md](headset.md).

The Revit server also keeps a legacy single-scene mode (`--scene` with no `--library`) used by the
synthetic scene and by maintainer scripts.

## The contract between the pieces

- `contracts/slug-cases.json` — how a document name becomes a folder name. Checked by
  `tools/addincheck` (C#) and `tools/ingetrazo/test_library.py` (Python).
- `contracts/library.schema.json` — the `meta.json` a publish writes.
- `docs/library-format.md` — routes and folder layout, the part third-party tools would depend on.

## What the tests cover

| Area | Test | Needs |
|---|---|---|
| Door cut-outs | `tools/test-wallcut` | .NET only |
| Collider fitting | `tools/test-planrect` | .NET only |
| Occlusion bake | `tools/test-ao` | .NET only |
| Texture pipeline | `tools/test-textures` | .NET + WPF |
| Collision behaviour | `tools/test-collision.mjs` | Node + a generated scene |
| glTF validity | `tools/gltfcheck` | Node |
| IngeTrazo derivation | `tools/ingetrazo/test_derive.py` | Python |
| Library + routes | `tools/ingetrazo/test_library.py` | Python |
| Both servers agree | `tools/conformance.py` | .NET + Python |
| Add-in deployment | `tools/addincheck` | Windows + installed Revit |

The Revit API itself is proprietary and is not redistributed, so the add-in cannot be compiled on
a hosted CI runner. Its Revit-free parts are covered by the tools above, and `addincheck` is run by
the maintainer against a real installation.

## Deliberate non-goals

- No game engine and no APK: the headset's browser is the runtime.
- No global illumination (baked occlusion only), no reflections, no lightmaps yet.
- No cloud service: everything is local to the machine and its LAN.
- No authentication on the local server: it is a LAN tool, not an internet service.
