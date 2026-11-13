"""Hunt the amber (short) collider that blocks the top of a stair flight.

    python tools/diagnose_step.py <colliders.json>

An amber box in the viewer is a segment under 2 m tall (colliderview.js: height
>= 2.0 is blue, 0 < height < 2.0 is amber). At a stair head that is usually the
floor's own edge, standing in the walking band.
"""
import json
import math
import sys

m = json.load(open(sys.argv[1], encoding="utf-8"))
segs = m.get("segments") or []
surfs = m.get("surfaces") or []


def height(s):
    return (s.get("y1") or 0) - (s.get("y0") or 0)


def overlap_plan(s, t):
    """Do a segment's plan box and a surface's bbox meet?"""
    x0, x1 = sorted((s["x1"], s["x2"]))
    z0, z1 = sorted((s["z1"], s["z2"]))
    pad = (s.get("thickness") or 0.1) / 2
    return not (x1 + pad < t["minX"] or x0 - pad > t["maxX"]
                or z1 + pad < t["minZ"] or z0 - pad > t["maxZ"])


def in_poly(x, z, flat):
    inside = False
    n = len(flat) // 2
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


amber = [s for s in segs if 0 < height(s) < 2.0]
print(f"segments {len(segs)}   amber (0-2 m) {len(amber)}   surfaces {len(surfs)}")
print()

# A stair head: an amber segment whose TOP meets a walkable surface right above
# the place you are standing, and which has floor under its lower edge.
suspects = []
for s in amber:
    top = s["y1"]
    hits = [t for t in surfs
            if abs(t["y"] - top) <= 0.10 and overlap_plan(s, t)]
    if hits:
        suspects.append((s, hits))

print(f"amber segments whose top meets a walkable surface: {len(suspects)}")
for s, hits in suspects[:12]:
    x = (s["x1"] + s["x2"]) / 2
    z = (s["z1"] + s["z2"]) / 2
    print(f"  h={height(s):.2f}  y {s['y0']:.2f}..{s['y1']:.2f}  "
          f"from ({s['x1']:.1f},{s['z1']:.1f}) to ({s['x2']:.1f},{s['z2']:.1f})  "
          f"len={math.dist((s['x1'], s['z1']), (s['x2'], s['z2'])):.2f}  "
          f"surface above at y={hits[0]['y']:.2f}")
print()

# the stair itself: small quadrilateral surfaces climbing in height
treads = [t for t in surfs
          if (t["maxX"] - t["minX"]) * (t["maxZ"] - t["minZ"]) < 3.0
          and t.get("p") and len(t["p"]) >= 6]
treads.sort(key=lambda t: t["y"])
print(f"small surfaces (possible treads): {len(treads)}")
if treads:
    print("  lowest 5:", [(round(t['y'], 2), round(t['maxX'] - t['minX'], 2),
                           round(t['maxZ'] - t['minZ'], 2)) for t in treads[:5]])
    print("  highest 5:", [(round(t['y'], 2), round(t['maxX'] - t['minX'], 2),
                            round(t['maxZ'] - t['minZ'], 2)) for t in treads[-5:]])
