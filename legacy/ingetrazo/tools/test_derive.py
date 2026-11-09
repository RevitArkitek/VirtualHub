"""Standalone tests for the collider derivation in ingetrazo_vr.py.

No IngeTrazo, no Qt: the module is imported with a stub `tools.base.Tool`, and
`build_manifest` is fed plain face dicts — the same shape `collect_faces`
produces from the document.

Run:  python tools/test_derive.py
"""
from __future__ import annotations

import importlib.util
import math
import sys
import types
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))

# ---- stub the one IngeTrazo import the plugin makes at module level --------
tools = types.ModuleType("tools")
base = types.ModuleType("tools.base")


class Tool:            # the plugin only inherits from it
    name = ""
    shortcut = None
    description = ""
    uses_snap = False


base.Tool = Tool
sys.modules["tools"] = tools
sys.modules["tools.base"] = base

spec = importlib.util.spec_from_file_location("ingetrazo_vr", ROOT / "ingetrazo_vr.py")
vr = importlib.util.module_from_spec(spec)
spec.loader.exec_module(vr)


# ---------------------------------------------------------------------------
# Face builders (world space, Z up — what collect_faces yields)
# ---------------------------------------------------------------------------

def floor(x0, y0, x1, y1, z, holes=None):
    poly = [(x0, y0, z), (x1, y0, z), (x1, y1, z), (x0, y1, z)]
    return {"poly": poly, "holes": holes or [], "normal": (0, 0, 1),
            "area": (x1 - x0) * (y1 - y0), "cls": "", "interior": False}


def upsidedown_floor(x0, y0, x1, y1, z):
    f = floor(x0, y0, x1, y1, z)
    f["normal"] = (0, 0, -1)          # a ceiling
    return f


def wall(p0, p1, z0, z1, holes=None):
    """A vertical rectangular face from p0 to p1 (plan), z0..z1."""
    poly = [(p0[0], p0[1], z0), (p1[0], p1[1], z0),
            (p1[0], p1[1], z1), (p0[0], p0[1], z1)]
    n = plan_normal(p0, p1)
    return {"poly": poly, "holes": holes or [], "normal": n,
            "area": math.dist(p0, p1) * (z1 - z0), "cls": "", "interior": False}


def plan_normal(p0, p1):
    dx, dy = p1[0] - p0[0], p1[1] - p0[1]
    n = math.hypot(dx, dy) or 1.0
    return (-dy / n, dx / n, 0.0)


def hole(p0, p1, z0, z1):
    return [(p0[0], p0[1], z0), (p1[0], p1[1], z0),
            (p1[0], p1[1], z1), (p0[0], p1[1] if False else p1[1], z1),
            (p0[0], p0[1], z1)]


def rect_hole(x0, y0, x1, y1, z0, z1):
    return [(x0, y0, z0), (x1, y0, z0), (x1, y1, z0),
            (x1, y1, z1), (x0, y1, z1), (x0, y0, z1)]


def sloped(x0, y0, x1, y1, z0, z1):
    """A pitched face rising along +Y, triangulated exactly as the adapter does."""
    poly = [(x0, y0, z0), (x1, y0, z0), (x1, y1, z1), (x0, y1, z1)]
    tris = [[poly[0], poly[1], poly[2]], [poly[0], poly[2], poly[3]]]
    return {"poly": poly, "holes": [], "normal": vr._newell(poly), "area": 10.0,
            "cls": "", "interior": False, "tris": tris}


# ---------------------------------------------------------------------------
# Assertions
# ---------------------------------------------------------------------------

fails = []
def check(name, cond, detail=""):
    if cond:
        print(f"  ok   {name}")
    else:
        fails.append(name)
        print(f"  FAIL {name}  {detail}")


def cover(segments, axis, lo, hi, at):
    """Does any segment cover the interval [lo,hi] on `axis` at `at`?"""
    for s in segments:
        if axis == "x":
            a, b, c = s["x1"], s["x2"], s["z1"]
            fixed = s["z2"]
            if abs(c - fixed) > 1e-6 or abs(fixed - at) > 0.2:
                continue
        else:
            a, b, fixed = s["z1"], s["z2"], s["x1"]
            if abs(fixed - s["x2"]) > 1e-6 or abs(fixed - at) > 0.2:
                continue
        lo_s, hi_s = sorted((a, b))
        mid = (lo + hi) / 2
        if lo_s <= mid <= hi_s:
            return True
    return False


print("1. a closed room: 4 walls, 1 floor")
faces = [floor(0, 0, 10, 4, 0),
         wall((0, 0), (10, 0), 0, 2.8),
         wall((10, 0), (10, 4), 0, 2.8),
         wall((10, 4), (0, 4), 0, 2.8),
         wall((0, 4), (0, 0), 0, 2.8),
         upsidedown_floor(0, 0, 10, 4, 2.8)]
m = vr.build_manifest(faces)
check("4 wall runs", len(m["segments"]) == 4, m["segments"])
check("1 surface (ceiling ignored)", len(m["surfaces"]) == 1, m["surfaces"])
check("no terrain", len(m["terrain"]) == 0)
check("spawn on the floor at eye height",
      abs(m["spawn"][1] - 1.6) < 1e-6 and abs(m["spawn"][2]) < 6, m["spawn"])
check("segment height spans the wall", m["segments"][0]["y0"] == 0 and
      abs(m["segments"][0]["y1"] - 2.8) < 1e-6, m["segments"][0])

print("2. a wall with a doorway: the hole that reaches the floor is a gap")
faces = [floor(0, 0, 10, 4, 0),
         wall((0, 0), (10, 0), 0, 2.8, holes=[rect_hole(2.0, -0.1, 2.9, 0.1, 0.0, 2.1)])]
m = vr.build_manifest(faces)
check("doorway splits the run in two", len(m["segments"]) == 2, m["segments"])
check("no collider across the doorway",
      not cover(m["segments"], "x", 2.05, 2.85, 0.0), m["segments"])
check("collider left of the doorway", cover(m["segments"], "x", 0.5, 1.5, 0.0))
check("collider right of the doorway", cover(m["segments"], "x", 4.0, 9.0, 0.0))

print("3. a window above the floor does NOT open the wall")
faces = [floor(0, 0, 10, 4, 0),
         wall((0, 0), (10, 0), 0, 2.8, holes=[rect_hole(2.0, -0.1, 4.0, 0.1, 0.9, 2.1)])]
m = vr.build_manifest(faces)
check("one unbroken run", len(m["segments"]) == 1, m["segments"])
check("blocks across the window", cover(m["segments"], "x", 2.5, 3.5, 0.0))

print("4. a stair: three treads, three walkable surfaces")
faces = [floor(0, 0, 1, 1, 0.17), floor(0, 1, 1, 2, 0.34), floor(0, 2, 1, 3, 0.51)]
m = vr.build_manifest(faces)
check("3 surfaces", len(m["surfaces"]) == 3, len(m["surfaces"]))
check("treads at their own heights",
      sorted(s["y"] for s in m["surfaces"]) == [0.17, 0.34, 0.51],
      [s["y"] for s in m["surfaces"]])

print("5. a sloped roof goes out as terrain triangles, not a surface")
m = vr.build_manifest([sloped(0, 0, 4, 4, 0.0, 2.0)])
check("terrain triangles emitted", len(m["terrain"]) == 2, len(m["terrain"]))
check("triangles are 9 numbers", all(len(t) == 9 for t in m["terrain"]))
check("heights vary across the slope", m["terrain"][0][1] != m["terrain"][1][7],
      m["terrain"])

print("6. an opening tagged IfcDoor drops the leaf that sits in it")
leaf = wall((2.0, 0.0), (2.9, 0.0), 0.0, 2.1)
leaf["cls"] = "IfcDoor"
faces = [floor(0, 0, 10, 4, 0), wall((0, 0), (10, 0), 0, 2.8,
                                     holes=[rect_hole(2.0, -0.1, 2.9, 0.1, 0.0, 2.1)]),
         leaf]
m = vr.build_manifest(faces, openings=[(1.95, -0.2, 2.95, 0.2)])
check("the leaf adds no collider in the doorway",
      not cover(m["segments"], "x", 2.05, 2.85, 0.0), m["segments"])

print("7. plan orientation: IngeTrazo +Y is viewer −Z")
m = vr.build_manifest([wall((0, 0), (0, 5), 0, 2.8)])
seg = m["segments"][0]
check("a wall along +Y runs along −Z",
      abs(seg["z1"]) < 1e-9 and abs(seg["z2"] + 5) < 1e-6, seg)

print("8. an empty model degrades to a warning, not an exception")
m = vr.build_manifest([])
check("warnings, no crash", m["warnings"] and m["segments"] == [], m["warnings"])

print("9. options: walls off leaves the floors alone")
m = vr.build_manifest([floor(0, 0, 4, 4, 0), wall((0, 0), (4, 0), 0, 2.8)],
                      options={"walls": False})
check("no segments", m["segments"] == [], m["segments"])
check("floor still there", len(m["surfaces"]) == 1)

print("10. a short vertical face with no floor-level edge blocks nothing")
# a plinth side: 0.4 m tall, its bottom edge sits at its own minimum, but the
# fallback path (longest plan span) must respect MIN_WALL_H too
short = wall((0, 0), (1.2, 0), 0.0, 0.4)
short["poly"] = [(0, 0, 0.1), (1.2, 0, 0.1), (1.2, 0, 0.5), (0, 0, 0.5)]
m = vr.build_manifest([floor(0, 0, 4, 4, 0), short])
check("short face filtered by height in both paths", m["segments"] == [],
      m["segments"])

print("11. a slab edge at the head of a stair is a step, not a wall")
treads = [floor(0, 0.0, 1.2, 0.4, 2.6), floor(0, 0.4, 1.2, 0.8, 2.8),
          floor(0, 0.8, 1.2, 1.2, 3.0)]
upper = floor(0, 1.2, 1.2, 3.0, 3.2)
edge = wall((0, 1.2), (1.2, 1.2), 2.6, 3.2)      # 0.6 m thick slab edge
m = vr.build_manifest(treads + [upper, edge])
check("no collider across the stair head", len(m["segments"]) == 0, m["segments"])
check("the report says so", any("slab edge" in w for w in m["warnings"]), m["warnings"])

print("12. the same edge with nothing to step from still blocks")
m = vr.build_manifest([edge, floor(0, 1.2, 1.2, 3.0, 3.2)])
check("edge kept with no step below it", len(m["segments"]) == 1, m["segments"])

print("13. a storey-high wall is never mistaken for a slab edge")
m = vr.build_manifest([floor(0, 0, 4, 4, 0), wall((0, 0), (4, 0), 0, 2.8),
                       floor(0, -2, 4, 0, 2.8)])
check("wall to the floor above still blocks",
      any(s["y1"] - s["y0"] > 2.0 for s in m["segments"]), m["segments"])

print("14. a parapet standing on a floor still blocks")
m = vr.build_manifest([floor(0, 0, 4, 4, 0), wall((0, 0), (4, 0), 0, 0.9)])
check("parapet kept", len(m["segments"]) == 1, m["segments"])

print("15. a stairwell floor reaches the viewer as terrain, and still counts")
# A floor with an opening cannot be one polygon, so it ships as triangles.
floor_tri = {"poly": [(-1.0, 0.0, 3.2), (2.6, 0.0, 3.2), (2.6, 4.0, 3.2),
                      (-1.0, 4.0, 3.2)],
             "holes": [[(-0.5, 0.5, 3.2), (0.5, 0.5, 3.2), (0.5, 1.5, 3.2),
                        (-0.5, 1.5, 3.2)]],
             "normal": (0, 0, 1), "area": 14.4, "cls": "", "interior": False,
             "tris": [[(-1.0, 0.0, 3.2), (2.6, 0.0, 3.2), (2.6, 4.0, 3.2)],
                      [(-1.0, 0.0, 3.2), (2.6, 4.0, 3.2), (-1.0, 4.0, 3.2)]]}
tread = floor(2.6, 0.0, 3.8, 1.2, 3.0)
edge2 = wall((2.6, 0.0), (2.6, 1.0), 2.6, 3.2)     # the slab edge at the stair head
m = vr.build_manifest([floor_tri, tread, edge2])
check("the floor is terrain, not a surface",
      all(abs(s["y"] - 3.2) > 0.01 for s in m["surfaces"])
      and len(m["terrain"]) >= 2, (len(m["surfaces"]), len(m["terrain"])))
check("its slab edge is walked as a step", m["segments"] == [], m["segments"])
check("and reported", any("slab edge" in w for w in m["warnings"]), m["warnings"])

print()
if fails:
    print(f"FAILED: {len(fails)} — {', '.join(fails)}")
    sys.exit(1)
print("all derivation tests passed")
