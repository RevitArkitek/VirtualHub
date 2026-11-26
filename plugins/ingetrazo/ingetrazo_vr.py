# SPDX-License-Identifier: GPL-3.0-or-later
"""IngeTrazo VR — export the open model and walk it in a headset browser.

The IngeTrazo side of the RevitVR workflow: press a button, get the model in
front of you on a Quest 3, walls solid and doorways passable.

Two halves, both borrowed:

* the scene — ``formats.gltf.save_glb`` writes a self-contained GLB (materials,
  embedded textures, Y-up, metres), the same call Render ▸ Blender makes;
* the viewer — RevitVR's WebXR page and collision engine, unchanged, served
  from ``www/`` beside this file.

What is new is the collider derivation (``build_manifest``): IngeTrazo has no
parametric walls, doors or railings, so the collision world is read off the
geometry itself. An upward face is somewhere you can stand; a vertical face
taller than ``MIN_WALL_H`` is something you cannot walk through; an opening
that reaches the floor (the face's own hole loop) splits the wall run instead
of blocking it. Faces tagged ``IfcDoor`` / ``IfcWindow`` are dropped as
obstacles, so a leaf or a pane left in the opening does not seal the doorway.

The scene is only ever read on the GUI thread; the server thread touches the
export directory, never the document.
"""
from __future__ import annotations

import json
import math
import os
import shutil
import socket
import subprocess
import threading
import time
import traceback
import unicodedata
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import quote

from tools.base import Tool

APP_KEY = "ingetrazo_vr"
EYE_HEIGHT = 1.6          # metres, matches the viewer's eye height
DEFAULT_PORT = 8770       # deliberately not RevitVR's 8765, so both can run

# Collider derivation, in metres. Small faces are painting, not building.
MIN_SURFACE_AREA = 0.05   # m² — below this a face is trim, not a floor
MIN_WALL_AREA = 0.04      # m² — a wall face smaller than this is a detail
MIN_WALL_H = 0.55         # m — shorter vertical faces are skirting/kerbs
MIN_RUN_LEN = 0.25        # m — a wall run shorter than this is a jamb
SEG_THICKNESS = 0.10      # m — a plane per face; a wall's two faces make its depth
FLOOR_TOL = 0.06          # m — a hole within this of the face's bottom reaches the floor
FLAT = 0.985              # n.z above this is a horizontal face
UPRIGHT = 0.85            # n.z below this is a vertical face
STEP_DROP = 0.47          # m — a slab edge this close to a floor below is a step
OPENING_CLASSES = ("IfcDoor", "IfcWindow")
# Layer names that mean "this is a way through", matched case-insensitively
# as a substring, so Puertas / Ventanas / Doors-1 all count.
OPENING_HINTS = ("door", "puerta", "window", "ventana", "opening", "hueco")


# ---------------------------------------------------------------------------
# Where things live
# ---------------------------------------------------------------------------

def _home() -> Path:
    return Path(os.environ.get("APPDATA") or Path.home() / ".config")


def data_dir() -> Path:
    """Exports and the served viewer live here (never inside the plugin)."""
    base = os.environ.get("LOCALAPPDATA") or os.environ.get("XDG_CACHE_HOME")
    root = Path(base) / "IngeTrazo" if base else Path.home() / ".cache" / "ingetrazo"
    return root / "VR"


def export_dir() -> Path:
    return data_dir() / "export"


def www_dir() -> Path:
    """The web app (library homepage + viewer). ``INGETRAZO_VR_WWW`` overrides it."""
    env = os.environ.get("INGETRAZO_VR_WWW")
    if env:
        return Path(env)
    # In the repository the web app is ../../web from plugins/ingetrazo; when installed it is copied
    # to %APPDATA%\ingetrazo\ingetrazo_vr_www, which is the runtime name this keeps for old installs.
    repo_web = Path(__file__).resolve().parents[2] / "web"
    for cand in (repo_web,
                 data_dir() / "www",
                 _home() / "ingetrazo" / "ingetrazo_vr_www"):
        if (cand / "index.html").is_file():
            return cand
    return data_dir() / "www"


# ---------------------------------------------------------------------------
# The shared model library
# ---------------------------------------------------------------------------
# Both plugins publish here, so one homepage lists every model either of them exported. A model's
# identity is its file name: exporting the same document again replaces its folder, which is what
# keeps the list free of duplicates.

def library_dir() -> Path:
    base = os.environ.get("LOCALAPPDATA") or os.environ.get("XDG_CACHE_HOME")
    env = os.environ.get("VRHUB_LIBRARY")
    if env:
        return Path(env)
    root = Path(base) / "VRHub" if base else Path.home() / ".cache" / "vrhub"
    return root / "library"


def slugify(name) -> str:
    """The URL- and folder-safe form of a document name: 'Casa Árboles' → 'casa-arboles'.

    Must match ``ModelLibrary.Slugify`` in RevitVR: the same project exported from either plugin
    is meant to land on the same card, not two that differ by an accent.
    """
    text = unicodedata.normalize("NFKD", str(name or "").strip())
    out = []
    for ch in text:
        if unicodedata.combining(ch):
            continue
        if ch.isascii() and ch.isalnum():
            out.append(ch.lower())
        elif out and out[-1] != "-":
            out.append("-")
    slug = "".join(out).strip("-")
    if len(slug) > 60:
        slug = slug[:60].rstrip("-")
    return slug or "model"


SCENE_FILES = ("scene.glb", "colliders.json", "railings.json", "textures.json")


def publish_library(out_dir, *, name, file_name=None, file_path=None, view=None,
                    triangles=0, source="ingetrazo", source_label="IngeTrazo") -> str:
    """Copy an export into the library, one folder per source file name.

    Metadata is written last, so an interrupted publish is a folder the homepage ignores rather
    than one it lists with no scene.
    """
    name = _strip_brand(name)
    slug = slugify(name)
    file_name = _strip_brand(file_name) if file_name else (Path(file_path).name if file_path else name)
    entry = library_dir() / slug
    scene = entry / "scene"
    scene.mkdir(parents=True, exist_ok=True)

    for fname in SCENE_FILES:
        src, dst = Path(out_dir) / fname, scene / fname
        if src.is_file():
            tmp = dst.with_suffix(dst.suffix + ".tmp")
            shutil.copyfile(src, tmp)
            os.replace(tmp, dst)          # a viewer may be reading the old one
        elif dst.is_file():
            dst.unlink()                  # a sidecar the previous export wrote and this one did not

    meta = {
        "slug": slug,
        "name": name,
        "source": source,
        "sourceLabel": source_label,
        "fileName": file_name or name,
        "filePath": file_path,
        "view": view,
        "exportedUtc": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "triangles": triangles,
        "generator": "IngeTrazo VR",
    }
    tmp = entry / "meta.json.tmp"
    tmp.write_text(json.dumps(meta, indent=2, ensure_ascii=False), encoding="utf-8")
    os.replace(tmp, entry / "meta.json")
    return slug


def list_models() -> list:
    """What the homepage shows. Reads the folder each time, so an export appears immediately."""
    root = library_dir()
    models = []
    if not root.is_dir():
        return models
    for entry in root.iterdir():
        try:
            if not entry.is_dir():
                continue
            meta_path = entry / "meta.json"
            glb, col = entry / "scene" / "scene.glb", entry / "scene" / "colliders.json"
            if not (meta_path.is_file() and glb.is_file() and col.is_file()):
                continue
            meta = json.loads(meta_path.read_text(encoding="utf-8"))
        except Exception:
            continue                   # half-written publish: not a model yet
        models.append({
            "slug": entry.name,
            "name": meta.get("name") or entry.name,
            "source": meta.get("source") or "other",
            "sourceLabel": meta.get("sourceLabel"),
            "fileName": meta.get("fileName"),
            "filePath": meta.get("filePath"),
            "view": meta.get("view"),
            "exportedUtc": meta.get("exportedUtc") or datetime.fromtimestamp(
                col.stat().st_mtime, timezone.utc).isoformat(timespec="seconds"),
            "triangles": meta.get("triangles"),
            "glbBytes": glb.stat().st_size,
            "colliderBytes": col.stat().st_size,
        })
    models.sort(key=lambda m: m.get("exportedUtc") or "", reverse=True)
    return models


# ---------------------------------------------------------------------------
# Collider derivation — pure geometry, no IngeTrazo imports (unit-testable)
# ---------------------------------------------------------------------------

def _unit(v):
    n = math.sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2])
    if n < 1e-12:
        return (0.0, 0.0, 0.0)
    return (v[0] / n, v[1] / n, v[2] / n)


def _newell(poly):
    """Area-weighted normal in double precision (a flat face must be flat)."""
    nx = ny = nz = 0.0
    n = len(poly)
    for i in range(n):
        x0, y0, z0 = poly[i]
        x1, y1, z1 = poly[(i + 1) % n]
        nx += (y0 - y1) * (z0 + z1)
        ny += (z0 - z1) * (x0 + x1)
        nz += (x0 - x1) * (y0 + y1)
    return _unit((nx, ny, nz))


def _poly_area(poly):
    """Plan area of a polygon by the shoelace formula (metres²)."""
    n = len(poly)
    a = 0.0
    for i in range(n):
        x0, y0 = poly[i][0], poly[i][1]
        x1, y1 = poly[(i + 1) % n][0], poly[(i + 1) % n][1]
        a += x0 * y1 - x1 * y0
    return abs(a) * 0.5


def _up(p):
    """IngeTrazo world (Z up) → viewer space (X right, Y up, Z forward)."""
    return (p[0], p[2], -p[1])


def point_in_poly(x, z, flat):
    """Crossing-number test against a flat [x,z,x,z,…] polygon."""
    inside = False
    n = len(flat) // 2
    if n < 3:
        return False
    j = n - 1
    for i in range(n):
        xi, zi = flat[i * 2], flat[i * 2 + 1]
        xj, zj = flat[j * 2], flat[j * 2 + 1]
        if (zi > z) != (zj > z):
            t = (z - zi) / (zj - zi) if (zj - zi) else 0.0
            if x < xi + t * (xj - xi):
                inside = not inside
        j = i
    return inside


def _flat_poly(poly):
    out = []
    for p in poly:
        u = _up(p)
        out += [round(u[0], 4), round(u[2], 4)]
    return out


def _runs_along_bottom(poly, holes, opts):
    """Wall runs from one vertical face, in plan, with floor-reaching holes cut.

    Returns ``(q0, q1, y0, y1)`` tuples in IngeTrazo world coordinates (Z up):
    an opening that reaches the floor is a doorway and splits the run; a window
    or a soffit leaves it whole.
    """
    zmin = min(p[2] for p in poly)
    zmax = max(p[2] for p in poly)
    if zmax - zmin < opts["min_wall_h"]:
        return []
    runs = []
    n = len(poly)
    for i in range(n):
        p0, p1 = poly[i], poly[(i + 1) % n]
        if abs(p0[2] - zmin) > FLOOR_TOL or abs(p1[2] - zmin) > FLOOR_TOL:
            continue
        dx, dy = p1[0] - p0[0], p1[1] - p0[1]
        length = math.hypot(dx, dy)
        if length < opts["min_run_len"]:
            continue
        ux, uy = dx / length, dy / length
        cuts = []
        for h in holes:
            if min(q[2] for q in h) > zmin + FLOOR_TOL:
                continue                     # a window, not a way through
            ts = [(q[0] - p0[0]) * ux + (q[1] - p0[1]) * uy for q in h]
            lo, hi = max(0.0, min(ts)), min(length, max(ts))
            if hi - lo > 1e-6:
                cuts.append((lo, hi))
        spans = [(0.0, length)]
        for lo, hi in sorted(cuts):
            nxt = []
            for a, b in spans:
                if hi <= a or lo >= b:
                    nxt.append((a, b))
                    continue
                if lo > a:
                    nxt.append((a, lo))
                if hi < b:
                    nxt.append((hi, b))
            spans = nxt
        for a, b in spans:
            if b - a < opts["min_run_len"]:
                continue
            q0 = (p0[0] + ux * a, p0[1] + uy * a, zmin)
            q1 = (p0[0] + ux * b, p0[1] + uy * b, zmin)
            runs.append((q0, q1, zmin, zmax))
    return runs


def _fallback_run(poly, opts):
    """A vertical face with no bottom edge at its lowest point (a notch, a
    reveal): one run along its longest plan span, so it still blocks."""
    zmin = min(p[2] for p in poly)
    zmax = max(p[2] for p in poly)
    if zmax - zmin < opts["min_wall_h"]:
        return None
    best = None
    n = len(poly)
    for i in range(n):
        for j in range(i + 1, n):
            d = math.hypot(poly[j][0] - poly[i][0], poly[j][1] - poly[i][1])
            if best is None or d > best[0]:
                best = (d, i, j)
    if not best or best[0] < opts["min_run_len"]:
        return None
    _, i, j = best
    return ((poly[i][0], poly[i][1], zmin), (poly[j][0], poly[j][1], zmin),
            zmin, zmax)


def _walk_index(surfaces, terrain):
    """Every place you can stand, bucketed by height.

    Floors with an opening go out as terrain triangles rather than polygons —
    a stairwell floor is one — so the step rule has to read both or it misses
    exactly the floor a stair climbs onto.
    """
    idx = {}

    def add(y, minx, minz, maxx, maxz):
        idx.setdefault(int(math.floor(y / 0.25)), []).append(
            (y, minx, minz, maxx, maxz))

    for s in surfaces:
        add(s["y"], s["minX"], s["minZ"], s["maxX"], s["maxZ"])
    for t in terrain:
        xs, ys, zs = t[0::3], t[1::3], t[2::3]
        add(sum(ys) / 3.0, min(xs), min(zs), max(xs), max(zs))
    return idx


def _step_at(x, z, y1, walk):
    """Is (x, z) the edge of a floor you step onto, rather than a wall?

    True when a walkable surface meets the top of the face here AND another
    walkable surface sits within a step below that top. That is a slab edge
    over a stair — the floor is one riser up, and the step logic carries you
    over it. A parapet standing on a floor has no walkable surface at its top,
    and a storey-high wall is not short, so both still block.
    """
    keys = {int(math.floor(y / 0.25))
            for y in (y1 + 0.10, y1 - 0.02, y1 - STEP_DROP)}
    at_top = near_step = False
    for k in keys:
        for (y, minx, minz, maxx, maxz) in walk.get(k, ()):
            if y > y1 + 0.10 or y < y1 - STEP_DROP:
                continue
            dx = max(minx - x, 0.0, x - maxx)
            dz = max(minz - z, 0.0, z - maxz)
            pad = 0.35 if abs(y - y1) <= 0.10 else 0.6
            if math.hypot(dx, dz) > pad:
                continue
            if abs(y - y1) <= 0.10:
                at_top = True
            else:
                near_step = True
            if at_top and near_step:
                return True
    return False


def _wall_pieces(q0, q1, y0, y1, walk, min_len):
    """The parts of a run that are real barriers.

    A run is sampled along its length and the pieces that are floor edges are
    dropped, so a long slab edge that only meets a stair at one end loses just
    that end. Returns IngeTrazo-world ``(q0, q1)`` pairs.
    """
    length = math.hypot(q1[0] - q0[0], q1[1] - q0[1])
    if length < 1e-6:
        return []
    if y1 - y0 > 1.0:                       # a wall, never a slab edge
        return [(q0, q1)]
    ux, uy = (q1[0] - q0[0]) / length, (q1[1] - q0[1]) / length
    n = max(1, int(math.ceil(length / 0.4)))
    keep = []
    for i in range(n):
        t = (i + 0.5) * length / n
        x, z = q0[0] + ux * t, q0[1] + uy * t
        a = _up((x, z, y0))
        keep.append(not _step_at(a[0], a[2], y1, walk))
    pieces = []
    i = 0
    while i < n:
        if not keep[i]:
            i += 1
            continue
        j = i
        while j + 1 < n and keep[j + 1]:
            j += 1
        a, b = i * length / n, (j + 1) * length / n
        if b - a >= min_len:
            pieces.append(((q0[0] + ux * a, q0[1] + uy * a, y0),
                           (q0[0] + ux * b, q0[1] + uy * b, y0)))
        i = j + 1
    return pieces


def build_manifest(faces, *, name="IngeTrazo model", eye_height=EYE_HEIGHT,
                   openings=None, options=None):
    """Turn world-space faces into the viewer's collision manifest.

    ``faces`` — dicts: ``poly`` (list of (x,y,z), Z up, outer loop, ordered),
    ``holes`` (list of loops), ``tris`` (finely triangulated, for sloped faces
    and floors with openings), ``area``, ``cls`` (Ifc class or ""),
    ``interior`` (bool).

    ``openings`` — plan boxes (minx, miny, maxx, maxy) in IngeTrazo XY, from
    faces tagged IfcDoor/IfcWindow; wall runs inside them are dropped.
    """
    opts = {"min_wall_h": MIN_WALL_H, "min_run_len": MIN_RUN_LEN,
            "min_surface_area": MIN_SURFACE_AREA, "min_wall_area": MIN_WALL_AREA,
            "thickness": SEG_THICKNESS, "ignore_openings": True, "walls": True}
    opts.update(options or {})
    openings = [] if not opts["ignore_openings"] else list(openings or [])
    warnings = []

    segments, surfaces, terrain = [], [], []
    zmin_all, zmax_all = float("inf"), float("-inf")
    tris = 0
    solid_holes = 0
    step_edges = 0
    opening_runs = 0
    applied = []

    def in_opening(x, y):
        for (x0, y0, x1, y1) in openings:
            if x0 - 0.02 <= x <= x1 + 0.02 and y0 - 0.02 <= y <= y1 + 0.02:
                return True
        return False

    # Pass 1 — what you can stand on. The wall pass needs these to tell the
    # edge of a floor from a wall.
    for face in faces:
        poly = face.get("poly") or []
        if len(poly) < 3 or face.get("interior"):
            continue
        for p in poly:
            if p[2] < zmin_all:
                zmin_all = p[2]
            if p[2] > zmax_all:
                zmax_all = p[2]
        nrm = face.get("normal") or _newell(poly)
        if nrm[2] <= UPRIGHT:                              # not upward
            continue
        area = face.get("area")
        if area is None:
            area = _poly_area(poly)
        if area < opts["min_surface_area"]:
            continue
        holes = face.get("holes") or []
        tris_of_face = face.get("tris") or []
        if nrm[2] > FLAT and not holes:
            y = sum(p[2] for p in poly) / len(poly)
            flat = _flat_poly(poly)
            xs, zs = flat[0::2], flat[1::2]
            surfaces.append({"minX": min(xs), "minZ": min(zs),
                             "maxX": max(xs), "maxZ": max(zs),
                             "y": round(y, 4), "p": flat})
        elif tris_of_face:
            # sloped, or a floor with an opening: exact triangles, so a
            # stairwell stays a hole and a hillside keeps its shape
            for tri in tris_of_face:
                if len(tri) != 3:
                    continue
                pts = []
                for p in tri:
                    u = _up(p)
                    pts += [round(u[0], 3), round(u[1], 3), round(u[2], 3)]
                terrain.append(pts)
                tris += 2
        elif nrm[2] > FLAT:
            # the build cannot triangulate a hole, so the floor goes out
            # solid — walking over an opening is better than no floor
            solid_holes += 1
            y = sum(p[2] for p in poly) / len(poly)
            flat = _flat_poly(poly)
            xs, zs = flat[0::2], flat[1::2]
            surfaces.append({"minX": min(xs), "minZ": min(zs),
                             "maxX": max(xs), "maxZ": max(zs),
                             "y": round(y, 4), "p": flat})

    # Pass 2 — what stops you.
    walk = _walk_index(surfaces, terrain)
    for face in faces:
        poly = face.get("poly") or []
        if len(poly) < 3 or face.get("interior"):
            continue
        nrm = face.get("normal") or _newell(poly)
        nz = nrm[2]
        if nz > UPRIGHT or nz < -UPRIGHT:                 # ceiling or floor
            continue
        area = face.get("area")
        if area is None:
            area = _poly_area(poly)
        if not opts["walls"] or area < opts["min_wall_area"]:
            continue
        cls = face.get("cls") or ""

        runs = _runs_along_bottom(poly, face.get("holes") or [], opts)
        if not runs:
            fb = _fallback_run(poly, opts)
            runs = [fb] if fb else []
        for q0, q1, y0, y1 in runs:
            why = None
            if face.get("opening"):
                why = "layer:" + str(face.get("layer") or "?")
            elif openings and in_opening((q0[0] + q1[0]) / 2,
                                         (q0[1] + q1[1]) / 2):
                why = "opening"
            if why:
                # recorded so the viewer can draw what the rule removed: the
                # only way to tell a tag or a layer actually did something
                a, b = _up(q0), _up(q1)
                applied.append({"x1": round(a[0], 3), "z1": round(a[2], 3),
                                "x2": round(b[0], 3), "z2": round(b[2], 3),
                                "y0": round(y0, 3), "y1": round(y1, 3),
                                "why": why})
                opening_runs += 1
                continue
            pieces = _wall_pieces(q0, q1, y0, y1, walk, opts["min_run_len"])
            if not pieces:
                step_edges += 1
            for p0, p1 in pieces:
                a, b = _up(p0), _up(p1)
                seg = {"x1": round(a[0], 4), "z1": round(a[2], 4),
                       "x2": round(b[0], 4), "z2": round(b[2], 4),
                       "thickness": opts["thickness"],
                       "y0": round(y0, 4), "y1": round(y1, 4), "solid": True}
                if cls:
                    seg["cls"] = cls
                segments.append(seg)

    if zmin_all == float("inf"):
        zmin_all = zmax_all = 0.0

    spawn, forward, room = _spawn(surfaces, segments, eye_height)
    if 0.0 < room < 0.6:
        warnings.append(f"nowhere roomy to stand at the model's base "
                        f"({room:.1f} m of clearance) — spawned in front of it")
    if not segments:
        warnings.append("no wall colliders — every vertical face was filtered "
                        "out (lower MIN_WALL_H in ingetrazo_vr.py)")
    if not surfaces and not terrain:
        warnings.append("no walkable surface found — is the model Z-up with its "
                        "floors facing up?")
    if solid_holes:
        warnings.append(f"{solid_holes} floor(s) with an opening were walked as "
                        "solid — this build has no hole-aware triangulation")
    if step_edges:
        warnings.append(f"{step_edges} slab edge(s) at stair heads were walked "
                        "as steps, not walls")

    return {
        "name": name,
        "generator": "IngeTrazo VR",
        "generatedUtc": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "eyeHeight": eye_height,
        "modelMinY": round(zmin_all, 4),
        "modelMaxY": round(zmax_all, 4),
        "triangleCount": tris,
        "bakedLight": False,
        "spawn": [round(v, 4) for v in spawn],
        "spawnForward": [round(v, 4) for v in forward],
        "segments": segments,
        "surfaces": surfaces,
        "terrain": terrain,
        "openings_applied": applied,
        "opening_runs": opening_runs,
        "warnings": warnings,
    }


def _clearance(x, z, segs):
    """Distance from (x, z) to the nearest wall box — how much room to stand."""
    best = 1e9
    for s in segs:
        dx, dz = s["x2"] - s["x1"], s["z2"] - s["z1"]
        length = math.hypot(dx, dz)
        if length < 1e-9:
            continue
        ux, uz = dx / length, dz / length
        cx, cz = s["x1"] + dx / 2, s["z1"] + dz / 2
        rx, rz = x - cx, z - cz
        u = rx * ux + rz * uz
        v = -rx * uz + rz * ux
        du = max(0.0, abs(u) - length / 2)
        dv = max(0.0, abs(v) - (s.get("thickness") or 0.1) / 2)
        d = math.hypot(du, dv)
        if d < best:
            best = d
    return best


def _facing(x, z, segs):
    """The most open direction from (x, z): eight compass rays, longest wins.

    Facing the model's centre is wrong inside a portal — it points into the
    arch's own underside, a hand's width away. Facing down the longest clear
    ray walks you into the space, which is what a walkthrough wants.
    """
    best = (0.0, 3.0, 0.0, 0.0)          # distance, dirx, dirz
    for i in range(8):
        ang = i * math.pi / 4
        dx, dz = math.cos(ang), math.sin(ang)
        limit = 12.0
        for s in segs:
            sx, sz = s["x2"] - s["x1"], s["z2"] - s["z1"]
            length = math.hypot(sx, sz)
            if length < 1e-9:
                continue
            ux, uz = sx / length, sz / length
            cx, cz = s["x1"] + sx / 2, s["z1"] + sz / 2
            rx, rz = x - cx, z - cz
            ox, oz = rx * ux + rz * uz, -rx * uz + rz * ux
            dxu, dzu = dx * ux + dz * uz, -dx * uz + dz * ux
            half_l, half_t = length / 2, (s.get("thickness") or 0.1) / 2
            t0, t1 = 0.0, limit
            for o, d, half in ((ox, dxu, half_l), (oz, dzu, half_t)):
                if abs(d) < 1e-9:
                    if abs(o) > half:
                        t1 = -1.0
                        break
                    continue
                ta, tb = (-half - o) / d, (half - o) / d
                if ta > tb:
                    ta, tb = tb, ta
                t0, t1 = max(t0, ta), min(t1, tb)
                if t0 > t1:
                    break
            if t0 <= t1 < limit:
                limit = max(0.0, t0)
        if limit > best[0]:
            best = (limit, dx, dz)
    return best[1], best[2]


def _spawn(surfaces, segments, eye_height):
    """Stand where there is the most room: the point on the largest floors
    with the greatest clearance from any wall, looking toward the model.

    Picking a floor's centre instead put the camera inside a column on the
    first real model tried.
    """
    if not surfaces:
        return [0.0, eye_height, 0.0], [0.0, 0.0, -1.0], 0.0

    def area(s):
        return (s["maxX"] - s["minX"]) * (s["maxZ"] - s["minZ"])

    # Start on the model's lowest level, not the roomiest surface anywhere: the
    # top of an arch beams has more open air around it than the ground does.
    base = min(s["y"] for s in surfaces)
    floor_band = [s for s in surfaces if s["y"] <= base + 2.0] or surfaces

    best = None
    for s in sorted(floor_band, key=lambda s: -area(s))[:8]:
        y = s["y"]
        band = [t for t in segments
                if t["y0"] < y + eye_height and t["y1"] > y + 0.1
                and t["x1"] <= s["maxX"] + 3 and t["x2"] <= s["maxX"] + 3
                and t["x1"] >= s["minX"] - 3 and t["x2"] >= s["minX"] - 3
                and t["z1"] <= s["maxZ"] + 3 and t["z2"] <= s["maxZ"] + 3
                and t["z1"] >= s["minZ"] - 3 and t["z2"] >= s["minZ"] - 3]
        poly = s.get("p") or []
        n = 13
        for i in range(n):
            for j in range(n):
                x = s["minX"] + (s["maxX"] - s["minX"]) * (i + 0.5) / n
                z = s["minZ"] + (s["maxZ"] - s["minZ"]) * (j + 0.5) / n
                if poly and not point_in_poly(x, z, poly):
                    continue
                c = _clearance(x, z, band)
                if best is None or c > best[0] + 0.05 or (
                        abs(c - best[0]) <= 0.05 and area(s) > area(best[3])):
                    best = (c, x, z, s)
    if best is None:
        big = max(surfaces, key=area)
        return ([big["minX"], big["y"] + eye_height, big["minZ"]],
                [0.0, 0.0, -1.0], 0.0)

    c, cx, cz, s = best
    xs = [o["minX"] for o in surfaces] + [o["maxX"] for o in surfaces]
    zs = [o["minZ"] for o in surfaces] + [o["maxZ"] for o in surfaces]
    if c < 0.6:
        # Nowhere roomy to stand on the model's own base — a monument, a kerb,
        # a plinth. Stand in front of it on the same level, looking in.
        cx, cz, s = (min(xs) + max(xs)) / 2, min(zs) - 2.0, {"y": base}
    fx, fz = _facing(cx, cz, [t for t in segments
                              if t["y0"] < s["y"] + eye_height
                              and t["y1"] > s["y"] + 0.2])
    return [cx, s["y"] + eye_height, cz], [fx, 0.0, fz], c


# ---------------------------------------------------------------------------
# Reading the document (the only part that touches IngeTrazo)
# ---------------------------------------------------------------------------

def _triangulate(face):
    """Triangles of a face as world triples, using the engine's own
    triangulation when the build has one (it respects hole loops), else a fan
    from the outer loop."""
    fn = getattr(face, "triangulate", None)
    if fn is not None:
        try:
            out = [[(a.x(), a.y(), a.z()) for a in t] for t in fn()]
            if out:
                return out
        except Exception:
            pass
    v = [(p.x(), p.y(), p.z()) for p in face.vertices]
    return [[v[0], v[i], v[i + 1]] for i in range(1, len(v) - 1)]


def _layer_of(entity):
    """The layer name an entity carries, face or group, as text.

    A layer is a name, but an imported or older document can carry a number
    where one belongs (one file has faces tagged ``2097``, a layer
    the document does not even list). Text it here so a stray tag can never
    take the export down.
    """
    try:
        from core.layers import layer_of
        name = layer_of(entity)
    except Exception:
        attrs = getattr(entity, "attrs", None) or {}
        name = attrs.get("layer") or getattr(entity, "layer", None)
    if name is None:
        return ""
    return name if isinstance(name, str) else str(name)


def _opening_hints(options):
    raw = (options or {}).get("opening_layers")
    if raw is None:
        return list(OPENING_HINTS)
    if isinstance(raw, str):
        raw = raw.replace(";", ",").split(",")
    return [str(x).strip().lower() for x in raw if str(x).strip()]


def _is_opening_layer(name, hints):
    n = (name if isinstance(name, str) else str(name or "")).strip().lower()
    return bool(n) and any(h in n for h in hints)


def _group_opening_box(group):
    """Plan bounding box of a group's geometry in world space, or None."""
    from core.group import iter_placements
    xs, ys = [], []
    try:
        placements = list(iter_placements(group))
    except Exception:
        placements = [(group, None)]
    for piece, m in placements:
        mesh = getattr(piece, "mesh", None)
        if mesh is None:
            continue
        for v in mesh.vertices:
            try:
                p = v.position if m is None else m.map(v.position)
            except Exception:
                continue
            xs.append(p.x())
            ys.append(p.y())
    if not xs:
        return None
    return (min(xs), min(ys), max(xs), max(ys))


def _collect_group_openings(scene, hints):
    """Plan boxes of every group whose layer says "way through", however deep.

    IngeTrazo has no door objects: a door is faces, possibly grouped, possibly a
    component instance. The layer sits on the group, so the group's own geometry
    is what has to be excluded — and nested groups and component children are
    walked, not just the top level.
    """
    boxes, names, seen = [], {}, set()
    stack = list(getattr(scene, "groups", []) or [])
    while stack:
        g = stack.pop()
        if id(g) in seen:
            continue
        seen.add(id(g))
        lay = _layer_of(g)
        if _is_opening_layer(lay, hints):
            box = _group_opening_box(g)
            if box:
                boxes.append(box)
                names[lay] = names.get(lay, 0) + 1
        stack.extend(getattr(g, "children", None) or ())
    return boxes, names


def collect_faces(scene, options=None):
    """World-space faces for the collider pass, plus the opening boxes.

    Returns ``(faces, openings, notes, stats)``. Reads only — no undo step, no
    scene-changed notification.

    An opening is geometry you can walk through: anything tagged IfcDoor /
    IfcWindow, and anything on a layer whose name reads like one (see
    ``OPENING_HINTS``). Both are optional — a hole in the wall that reaches the
    floor is already a doorway without either.

    The API differs between the 0.3.x install and the current source:
    ``world_faces_painted`` and ``export_triangles`` are newer, so both are
    used when present and worked around when not.
    """
    from formats import meshexport as mx

    painted = getattr(mx, "world_faces_painted", None)
    if painted is not None:
        walker = painted(scene)
    else:
        walker = ((f, getattr(f, "attrs", {}) or {}) for f in mx.world_faces(scene))
    tri_fn = getattr(mx, "export_triangles", None)
    hints = _opening_hints(options)

    faces, openings, notes = [], [], []
    tagged = 0
    layer_faces, layer_groups = {}, {}
    for face, _attrs in walker:
        if getattr(face, "interior", False):
            continue                     # inside a solid, not a boundary
        try:
            poly = [(v.x(), v.y(), v.z()) for v in face.vertices]
        except Exception:
            continue
        if len(poly) < 3:
            continue
        holes = []
        for h in getattr(face, "hole_loops", None) or []:
            hp = [(v.position.x(), v.position.y(), v.position.z()) for v in h]
            if len(hp) >= 3:
                holes.append(hp)
        try:
            n = face.normal()
            nrm = (n.x(), n.y(), n.z())
        except Exception:
            nrm = _newell(poly)
        try:
            area = float(face.area())
        except Exception:
            area = _poly_area(poly)
        attrs = getattr(face, "attrs", {}) or {}
        tag = attrs.get("ifc") or {}
        cls = tag.get("class", "") if isinstance(tag, dict) else ""
        if cls:
            tagged += 1
        lay = _layer_of(face)
        is_opening = _is_opening_layer(lay, hints)
        if is_opening:
            layer_faces[lay] = layer_faces.get(lay, 0) + 1
        tris = None
        # Only faces that need triangles pay for them: sloped ground, and a
        # floor with an opening that must stay a hole you can fall through.
        if nrm[2] > UPRIGHT and (nrm[2] <= FLAT or holes):
            try:
                if tri_fn is not None:
                    tris = [[(a.x(), a.y(), a.z()) for a in t] for t in tri_fn(face)]
                else:
                    tris = _triangulate(face)
            except Exception:
                tris = None
        faces.append({"poly": poly, "holes": holes, "normal": nrm, "area": area,
                      "cls": cls, "layer": lay, "opening": is_opening,
                      "interior": bool(getattr(face, "interior", False)),
                      "tris": tris})


    # Doors and windows: their plan extent, so a leaf left in the opening does
    # not seal the doorway. World space, straight from the BIM layer.
    try:
        from core import bim
        for obj in bim.collect_objects(scene):
            if obj.get("class") not in OPENING_CLASSES:
                continue
            pts = [v.position for f in (obj.get("faces") or ()) for v in f.loop]
            if not pts:
                continue
            xs = [p.x() for p in pts]
            ys = [p.y() for p in pts]
            openings.append((min(xs), min(ys), max(xs), max(ys)))
    except Exception as exc:                    # never lose the export to this
        notes.append(f"BIM tags unreadable ({exc}) — doors are not opened")

    # Faces carry a layer of their own, but a group carries one for its whole
    # set of faces — and that is how a modelled "door" is most likely labelled.
    group_boxes, layer_groups = _collect_group_openings(scene, hints)
    openings.extend(group_boxes)

    ifc_openings = len(openings) - sum(layer_groups.values())
    stats = {"tagged": tagged, "ifc_openings": ifc_openings,
             "layer_faces": layer_faces, "layer_groups": layer_groups,
             "hints": hints}
    return faces, openings, notes, stats


def _strip_brand(text) -> str:
    """Drop IngeTrazo's own name from a document title.

    ``document.name`` reports the window title, "IngeTrazo — Burj Kalifa", while the library card
    already shows the app as a pill. Keeping the prefix makes every IngeTrazo card start with the
    same words and pushes the actual model name off the useful end of the line.
    """
    text = str(text or "").strip()
    for sep in (" — ", " – ", " - ", " | ", ": "):
        if sep in text:
            head, tail = text.split(sep, 1)
            if tail.strip() and head.strip().lower().startswith("ingetrazo"):
                return tail.strip()
    return text


def document_name(viewport):
    for getter in (lambda: Path(viewport.scene.path).stem,
                   lambda: viewport.document.name,
                   lambda: viewport.window().windowTitle()):
        try:
            v = getter()
            if v:
                return _strip_brand(Path(str(v)).stem)
        except Exception:
            continue
    return "IngeTrazo model"


def document_path(viewport):
    """The open file's full path, when the API exposes it — the card shows its name."""
    for getter in (lambda: viewport.scene.path,
                   lambda: getattr(viewport.document, "path", None)):
        try:
            v = getter()
            if v:
                return str(v)
        except Exception:
            continue
    return None


def _sniff_image(data):
    if data[:8] == b"\x89PNG\r\n\x1a\n":
        return "image/png"
    if data[:3] == b"\xff\xd8\xff":
        return "image/jpeg"
    if data[:4] == b"RIFF" and data[8:12] == b"WEBP":
        return "image/webp"
    return None


def fix_glb_image_mimes(path):
    """Correct image MIME types taken from file extensions rather than bytes.

    The GLB writer names an image by its path suffix; a PNG stored with a .jpg
    name then ships declared ``image/jpeg``. The validator calls that an error
    and a strict loader can refuse the whole file. The bytes are the truth.

    Returns the number of images corrected. Rewrites the file in place.
    """
    import struct

    path = Path(path)
    data = path.read_bytes()
    if len(data) < 20 or data[:4] != b"glTF":
        return 0
    total = struct.unpack_from("<I", data, 8)[0]
    chunks = []
    off = 12
    while off + 8 <= min(total, len(data)):
        length, ctype = struct.unpack_from("<I4s", data, off)
        body = off + 8
        chunks.append((ctype, body, length))
        off = body + length
    json_chunk = next((c for c in chunks if c[0] == b"JSON"), None)
    bin_chunk = next((c for c in chunks if c[0] == b"BIN\x00"), None)
    if json_chunk is None:
        return 0
    doc = json.loads(data[json_chunk[1]:json_chunk[1] + json_chunk[2]]
                     .decode("utf-8").rstrip("\x00 "))
    images = doc.get("images") or []
    views = doc.get("bufferViews") or []
    changed = 0
    for img in images:
        bv = img.get("bufferView")
        if bv is None or bin_chunk is None or bv >= len(views):
            continue
        v = views[bv]
        start = bin_chunk[1] + v.get("byteOffset", 0)
        n = v.get("byteLength", 0)
        blob = data[start:start + n]
        sniffed = _sniff_image(blob)
        if sniffed and sniffed != img.get("mimeType"):
            img["mimeType"] = sniffed
            changed += 1
    if not changed:
        return 0

    raw = json.dumps(doc, separators=(",", ":")).encode("utf-8")
    raw += b" " * ((4 - len(raw) % 4) % 4)
    out = bytearray(b"glTF" + struct.pack("<II", 2, 0))
    for ctype, body, length in chunks:
        if ctype == b"JSON":
            out += struct.pack("<I4s", len(raw), b"JSON") + raw
        else:
            out += struct.pack("<I4s", length, ctype) + data[body:body + length]
    struct.pack_into("<I", out, 8, len(out))
    path.write_bytes(bytes(out))
    return changed


def export(scene, *, name=None, write_glb=True, options=None, file_path=None, view=None):
    """Write scene.glb + colliders.json into the export directory, then publish to the library.

    GUI thread. The export directory is the "last export" the legacy viewer paths read; the library
    copy is what the homepage lists, keyed by the source file name so a re-export replaces it.
    """
    out = export_dir()
    out.mkdir(parents=True, exist_ok=True)
    t0 = time.time()
    notes = []

    glb_path = out / "scene.glb"
    if write_glb:
        from formats.gltf import save_glb
        save_glb(scene, glb_path)
        try:
            fixed = fix_glb_image_mimes(glb_path)
            if fixed:
                notes.append(f"{fixed} texture(s) were declared with the wrong MIME "
                             "type by the GLB writer and corrected")
        except Exception as exc:
            notes.append(f"could not check the GLB's image types ({exc})")

    faces, openings, read_notes, stats = collect_faces(scene, options)
    notes += read_notes
    manifest = build_manifest(faces, name=name or "IngeTrazo model",
                              openings=openings, options=options)
    n_layer = sum(stats["layer_groups"].values()) + len(stats["layer_faces"])
    names = sorted(set(list(stats["layer_groups"]) + list(stats["layer_faces"])))
    if stats["ifc_openings"] or n_layer:
        notes.append(
            f"openings: {stats['ifc_openings']} tagged IfcDoor/IfcWindow"
            + (f", {n_layer} on layer(s) {', '.join(names)}" if n_layer else ""))
    else:
        notes.append("openings: none — tag the door IfcDoor/IfcWindow, or put it "
                     "on a layer whose name contains "
                     + ", ".join(stats["hints"]))
    notes.append(f"{manifest['opening_runs']} wall run(s) dropped as openings")

    with open(out / "colliders.json", "w", encoding="utf-8") as fh:
        json.dump(manifest, fh, separators=(",", ":"))
    manifest["_notes"] = notes + manifest["warnings"]
    manifest["_glb_mb"] = (round(glb_path.stat().st_size / 1e6, 1)
                           if glb_path.is_file() else 0.0)
    manifest["_seconds"] = round(time.time() - t0, 1)
    manifest["_faces"] = len(faces)

    # Publish into the shared library the homepage lists. Keyed by the file name, so exporting the
    # same model again replaces its card. A failure here must not fail the export: the scene on disk
    # is complete and walkable either way.
    manifest["_slug"] = None
    if write_glb:
        try:
            manifest["_slug"] = publish_library(
                out,
                name=name or "Untitled",
                file_name=Path(file_path).name if file_path else (name or "Untitled"),
                file_path=file_path,
                view=view,
                triangles=int(manifest.get("triangleCount") or 0),
            )
        except Exception as exc:
            notes.append(f"could not add this export to the model library ({exc})")
            manifest["_notes"] = notes + manifest["warnings"]

    return manifest


# ---------------------------------------------------------------------------
# Serving: viewer at /, last export at /scene/ — the same routes RevitVR uses
# ---------------------------------------------------------------------------

_server = None
_server_port = None
_server_thread = None
_https_server = None
_https_port = None


def _make_handler(root_www: Path, root_scene: Path, library: Path | None = None):
    """Static viewer + the model library.

    Routes: ``/`` is the homepage, ``/viewer.html?model=<slug>`` is the viewer on one model,
    ``/models/<slug>/scene/<file>`` is that model's files, ``/api/models`` is the card list. The
    legacy ``/scene/`` mapping still serves the last export, which is what ``?scene=`` opens.
    """
    import mimetypes
    from http.server import SimpleHTTPRequestHandler

    lib = Path(library) if library else library_dir()

    class Handler(SimpleHTTPRequestHandler):
        def __init__(self, *a, **kw):
            super().__init__(*a, directory=str(root_www), **kw)

        # ---- library -------------------------------------------------------
        def _json(self, payload):
            body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Cache-Control", "no-store")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def _model_file(self, clean: str) -> bool:
            parts = [p for p in clean.split("/") if p]
            # /models/<slug>/scene/<file> — nothing else is a model file
            if len(parts) != 4 or parts[0] != "models" or parts[2] != "scene":
                return False
            slug, fname = parts[1], parts[3]
            if (slug in (".", "..") or fname in (".", "..")
                    or "/" in slug or "\\" in slug or "/" in fname or "\\" in fname
                    or ".." in slug or ".." in fname):
                self.send_error(404)
                return True
            path = (lib / slug / "scene" / fname).resolve()
            try:
                path.relative_to(lib.resolve())
            except ValueError:
                self.send_error(404)
                return True
            if not path.is_file():
                self.send_error(404)
                return True

            data = path.read_bytes()
            ctype = mimetypes.guess_type(path.name)[0] or "application/octet-stream"
            self.send_response(200)
            self.send_header("Content-Type", ctype)
            self.send_header("Cache-Control", "no-store, must-revalidate")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
            return True

        def do_GET(self):
            clean = self.path.split("?", 1)[0].split("#", 1)[0]
            if clean == "/api/models":
                return self._json({"models": list_models(), "library": str(lib)})
            if clean.startswith("/api/"):
                return self.send_error(404)
            if clean.startswith("/models/") and self._model_file(clean):
                return
            super().do_GET()

        # ---- static --------------------------------------------------------
        def translate_path(self, path):
            clean = path.split("?", 1)[0].split("#", 1)[0]
            if clean.startswith("/scene/"):
                base, rel = root_scene, clean[len("/scene/"):]
            else:
                base, rel = root_www, clean.lstrip("/")
            rel = rel or "index.html"
            target = (base / rel).resolve()
            try:
                target.relative_to(base.resolve())
            except ValueError:
                # Escape attempt. A path that does not exist gives the browser a 404; handing back
                # index.html instead would answer a traversal with 200 and hide the mistake.
                return str(base / ".outside-the-web-root")
            if target.is_dir():
                target = target / "index.html"
            return str(target)

        def end_headers(self):
            if self.path.startswith("/scene/"):
                self.send_header("Cache-Control", "no-store")
            super().end_headers()

        def log_message(self, fmt, *args):
            pass

    return Handler


def _port_in_use(port: int) -> bool:
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    s.settimeout(0.15)
    try:
        return s.connect_ex(("127.0.0.1", port)) == 0
    except OSError:
        return False
    finally:
        s.close()


def start_server(port=None):
    """Serve the viewer and the last export; returns (port, already_running)."""
    global _server, _server_port, _server_thread
    www = www_dir()
    if not (www / "index.html").is_file():
        raise RuntimeError(f"viewer not found at {www} — run install.ps1")
    if _server is not None and _server_port:
        return _server_port, True

    from functools import partial
    from http.server import ThreadingHTTPServer

    # Windows lets a second socket bind a port that is already listening when
    # SO_REUSEADDR is set (Python's default), and connections then go to the
    # older server: the page would silently show someone else's scene.
    class Server(ThreadingHTTPServer):
        allow_reuse_address = False
        daemon_threads = True

    port = port or DEFAULT_PORT
    last = None
    for cand in range(port, port + 20):
        if _port_in_use(cand):
            continue
        try:
            srv = Server(("0.0.0.0", cand),
                         partial(_make_handler(www, export_dir(), library_dir())))
            break
        except OSError as exc:
            last = exc
    else:
        raise RuntimeError(f"no free port near {port}: {last}")
    _server, _server_port = srv, cand
    _server_thread = threading.Thread(target=srv.serve_forever, daemon=True,
                                      name="ingetrazo-vr-server")
    _server_thread.start()
    return cand, False


def stop_server():
    global _server, _server_port, _https_server, _https_port
    for srv in (_server, _https_server):
        if srv is not None:
            try:
                srv.shutdown()
                srv.server_close()
            except Exception:
                pass
    _server = _https_server = None
    _server_port = _https_port = None


# ---- HTTPS: the headset refuses WebXR on a plain-http origin -----------------
#
# WebXR needs a secure context. `http://localhost` counts as one (that is why
# adb reverse works with no certificate), but `http://192.168.x.x` does not —
# the page loads and Enter VR stays dead. So the LAN side is served over https
# with a self-signed certificate the headset accepts once.

def _openssl():
    cands = [shutil.which("openssl"),
             r"C:\Program Files\Git\usr\bin\openssl.exe",
             r"C:\Program Files\Git\mingw64\bin\openssl.exe",
             r"C:\Program Files\OpenSSL-Win64\bin\openssl.exe"]
    for c in cands:
        if c and Path(c).is_file():
            return c
    return None


def ensure_cert(force=False):
    """A self-signed certificate covering this PC's LAN address.

    Returns ``(cert.pem, key.pem)`` or ``None`` when no openssl is available.
    Regenerated when the address changes, since a LAN cert names the address.
    """
    cert = data_dir() / "cert.pem"
    key = data_dir() / "key.pem"
    meta = data_dir() / "cert.json"
    ips = sorted({lan_address(), "127.0.0.1"})
    if not force and cert.is_file() and key.is_file():
        try:
            saved = json.loads(meta.read_text(encoding="utf-8")).get("ips")
        except Exception:
            saved = None
        if saved == ips:
            return cert, key
    exe = _openssl()
    if not exe:
        return None
    data_dir().mkdir(parents=True, exist_ok=True)
    san = "DNS:localhost," + ",".join("IP:" + ip for ip in ips)
    rc, out = _run([exe, "req", "-x509", "-newkey", "rsa:2048", "-nodes",
                    "-keyout", str(key), "-out", str(cert), "-days", "3650",
                    "-subj", "/CN=IngeTrazo VR",
                    "-addext", "subjectAltName=" + san], timeout=90)
    if rc != 0 or not cert.is_file() or not key.is_file():
        return None
    try:
        meta.write_text(json.dumps({"ips": ips, "san": san}), encoding="utf-8")
    except Exception:
        pass
    return cert, key


def start_https(port=None, force_cert=False):
    """Serve the same viewer over TLS for the headset; returns the port or None."""
    global _https_server, _https_port
    if _https_server is not None and _https_port:
        return _https_port
    pair = ensure_cert(force=force_cert)
    if pair is None:
        return None
    cert, key = pair
    www = www_dir()
    if not (www / "index.html").is_file():
        return None

    import ssl
    from functools import partial
    from http.server import ThreadingHTTPServer

    class Server(ThreadingHTTPServer):
        allow_reuse_address = False
        daemon_threads = True

    port = port or (DEFAULT_PORT + 1)
    for cand in range(port, port + 20):
        if _port_in_use(cand):
            continue
        try:
            srv = Server(("0.0.0.0", cand),
                         partial(_make_handler(www, export_dir(), library_dir())))
        except OSError:
            continue
        try:
            ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
            ctx.load_cert_chain(str(cert), str(key))
            srv.socket = ctx.wrap_socket(srv.socket, server_side=True)
        except Exception:
            srv.server_close()
            return None
        _https_server, _https_port = srv, cand
        threading.Thread(target=srv.serve_forever, daemon=True,
                         name="ingetrazo-vr-https").start()
        return cand
    return None


def lan_address():
    """This PC's Wi-Fi address — not the first IPv4, which is often a VPN."""
    best = None
    try:
        for info in socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET):
            ip = info[4][0]
            if ip.startswith(("127.", "10.5.", "10.100.")):
                continue
            best = best or ip
    except OSError:
        pass
    if not best:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        try:
            s.connect(("192.168.86.1", 1))
            best = s.getsockname()[0]
        except OSError:
            best = "127.0.0.1"
        finally:
            s.close()
    return best


# ---------------------------------------------------------------------------
# adb — the one-click path (localhost on the headset, so WebXR is allowed)
# ---------------------------------------------------------------------------

def find_adb():
    cands = [shutil.which("adb"),
             str(Path(os.environ.get("LOCALAPPDATA", "")) /
                 "Android" / "Sdk" / "platform-tools" / "adb.exe")]
    for c in cands:
        if c and Path(c).is_file():
            return c
    return None


def _run(cmd, timeout=20):
    try:
        p = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout,
                           creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        return p.returncode, (p.stdout or "") + (p.stderr or "")
    except Exception as exc:
        return -1, str(exc)


def headset_url(port, https_port=None, path="/"):
    """The URL to type on the headset: https when we have a certificate."""
    if https_port:
        return f"https://{lan_address()}:{https_port}{path}"
    return f"http://{lan_address()}:{port}{path}"


def open_on_headset(port, https_port=None, path="/"):
    """adb reverse + open the page. Returns (ok, message)."""
    url = headset_url(port, https_port, path)
    adb = find_adb()
    if not adb:
        return False, ("adb not found — open " + url + " in the headset browser "
                       "(or install Android platform-tools)")
    rc, out = _run([adb, "devices"])
    serial = None
    for line in out.splitlines():
        parts = line.split()
        if len(parts) >= 2 and parts[1] == "device":
            serial = parts[0]
            break
    if not serial:
        return False, ("no headset over adb — enable USB (or wireless) debugging "
                       "and plug it in, or open " + url + " in the headset browser")
    rc, out = _run([adb, "-s", serial, "reverse", f"tcp:{port}", f"tcp:{port}"])
    if rc != 0 or "error" in out.lower():
        return False, f"adb reverse failed: {out.strip()}"
    rc, out = _run([adb, "-s", serial, "shell", "am", "start", "-a",
                    "android.intent.action.VIEW", "-d", f"http://localhost:{port}{path}"])
    if rc != 0:
        return False, f"could not open the browser on the headset: {out.strip()}"
    return True, f"open on {serial}: http://localhost:{port}{path}"


# ---------------------------------------------------------------------------
# The dialog
# ---------------------------------------------------------------------------

def _report_lines(m):
    return [
        f"scene:      {export_dir() / 'scene.glb'}  ({m['_glb_mb']} MB)",
        f"faces read: {m['_faces']}    terrain triangles: {m['triangleCount']}",
        f"colliders:  {len(m['segments'])} wall runs, {len(m['surfaces'])} surfaces, "
        f"{len(m['terrain'])} terrain triangles",
        f"export:     {m['_seconds']} s",
    ] + ["! " + w for w in m["_notes"]]


def _make_dialog(viewport):
    from PySide6.QtCore import QUrl
    from PySide6.QtGui import QDesktopServices
    from PySide6.QtWidgets import (QCheckBox, QDialog, QHBoxLayout, QLabel,
                                   QLineEdit, QMessageBox, QPlainTextEdit,
                                   QPushButton, QSpinBox, QVBoxLayout)

    class VrDialog(QDialog):
        def __init__(self):
            super().__init__(viewport.window())
            self.setWindowTitle("IngeTrazo VR")
            self.setMinimumWidth(600)
            self.port = None
            self.https = None
            lay = QVBoxLayout(self)

            info = QLabel(
                "Export the open model and walk it in the headset's browser.\n"
                "Hidden objects stay out. An opening that reaches the floor is passable; "
                "a face tagged IfcDoor / IfcWindow never blocks.")
            info.setWordWrap(True)
            lay.addWidget(info)

            row = QHBoxLayout()
            row.addWidget(QLabel("Port"))
            self.port_spin = QSpinBox()
            self.port_spin.setRange(1024, 65535)
            self.port_spin.setValue(DEFAULT_PORT)
            row.addWidget(self.port_spin)
            self.opening_layers = QLineEdit(", ".join(OPENING_HINTS))
            self.opening_layers.setToolTip(
                "Geometry on a layer whose name contains any of these is a way "
                "through, like a face tagged IfcDoor.")
            lay_row = QHBoxLayout()
            lay_row.addWidget(QLabel("Opening layers"))
            lay_row.addWidget(self.opening_layers, stretch=1)
            lay.addLayout(lay_row)

            self.untagged = QCheckBox("Untagged geometry blocks too")
            self.untagged.setChecked(True)
            self.untagged.setToolTip(
                "On: every vertical face is a wall. Off: only IFC-tagged faces are.")
            row.addWidget(self.untagged)
            row.addStretch(1)
            lay.addLayout(row)

            self.log = QPlainTextEdit()
            self.log.setReadOnly(True)
            self.log.setMinimumHeight(190)
            lay.addWidget(self.log, stretch=1)

            btns = QHBoxLayout()
            headset = QPushButton("Export && open on Quest")
            headset.setDefault(True)
            desktop = QPushButton("Export && desktop preview")
            again = QPushButton("Re-export only")
            close = QPushButton("Close")
            for b in (headset, desktop, again):
                btns.addWidget(b)
            btns.addStretch(1)
            btns.addWidget(close)
            lay.addLayout(btns)

            headset.clicked.connect(lambda: self.do_export("quest"))
            desktop.clicked.connect(lambda: self.do_export("desktop"))
            again.clicked.connect(lambda: self.do_export(None))
            close.clicked.connect(self.close)

        def do_export(self, where):
            self.setEnabled(False)
            self.log.setPlainText("exporting…")
            self.repaint()
            try:
                m = export(viewport.scene, name=document_name(viewport),
                           file_path=document_path(viewport),
                           options={"walls": self.untagged.isChecked(),
                                    "opening_layers": self.opening_layers.text()})
            except Exception as exc:
                detail = traceback.format_exc()
                saved = ""
                try:
                    log = export_dir() / "export-error.log"
                    log.write_text(detail, encoding="utf-8")
                    saved = f"\n\nfull traceback: {log}"
                except Exception:
                    pass
                self.log.setPlainText(detail)
                QMessageBox.warning(self, "IngeTrazo VR", f"Export failed:\n{exc}{saved}")
                self.setEnabled(True)
                return
            slug = m.get("_slug")
            model_path = f"/viewer.html?model={quote(slug)}" if slug else "/viewer.html"
            text = "\n".join(_report_lines(m))
            try:
                port, reused = start_server(self.port_spin.value())
                self.port = port
                text += (f"\nthis PC:   http://localhost:{port}/"
                         + ("  (already running)" if reused else ""))
                if slug:
                    text += f"\nmodel:     http://localhost:{port}{model_path}"
                text += f"\nlibrary:   {len(list_models())} model(s) listed at /"
                https = start_https(port + 1)
                self.https = https
                if https:
                    text += (f"\nheadset:   https://{lan_address()}:{https}/"
                             "\n           accept the certificate warning once, then "
                             "Enter VR works")
                else:
                    text += ("\nheadset:   no https (openssl not found) — connect the "
                             "headset over USB and use adb")
            except Exception as exc:
                text += f"\nserver:  FAILED — {exc}"
            if where == "desktop" and self.port:
                QDesktopServices.openUrl(QUrl(f"http://localhost:{self.port}{model_path}"))
                text += "\ndesktop preview opened in your browser"
            if where == "quest" and self.port:
                ok, msg = open_on_headset(self.port, self.https, model_path)
                text += "\nheadset:   " + msg + ("" if ok else "  (the export is still served)")
            self.log.setPlainText(text)
            self.setEnabled(True)

    return VrDialog()


def _run_dialog(viewport):
    _make_dialog(viewport).exec()


# ---------------------------------------------------------------------------
# Plugin entry points
# ---------------------------------------------------------------------------

class ExportVrTool(Tool):
    """Extensions ▸ Export to VR: export the model and open it in the headset."""
    name = "Export to VR"
    shortcut = None
    description = ("Export this model and walk it in the Quest browser — walls "
                   "solid, doorways passable.")
    uses_snap = False

    def on_activate(self, viewport) -> None:
        _run_dialog(viewport)

    def on_deactivate(self, viewport) -> None:
        pass


def setup(app):
    """Serve the last export from startup, so the URL keeps working after a
    restart: the page is useless if the port only exists after an export."""
    def _boot():
        try:
            # Serve at startup whenever there is anything to show: the last export, or a library
            # full of models from either plugin. The homepage must work after a restart, not only
            # right after an export.
            if (export_dir() / "scene.glb").is_file() or list_models():
                start_server()
                start_https()
        except Exception:
            pass                      # never let a port stop IngeTrazo starting

    try:
        from PySide6.QtCore import QTimer
        QTimer.singleShot(2500, _boot)
    except Exception:
        pass
    return None
